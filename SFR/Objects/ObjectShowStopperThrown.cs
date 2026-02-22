using System;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Objects;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFR.Fighter;
using SFR.Helper;
using SFR.Misc;
using Explosion = SFD.Explosion;
using Player = SFD.Player;

namespace SFR.Objects;

internal sealed class ObjectShowStopperThrown : ObjectGrenadeThrown
{
    // Lightning burst parameters
    private const float BurstRadius = 50f;
    private const float ElectrocutionDuration = 6000f; // 6 seconds in ms

    // Electric field effect parameters (visual aura around the grenade before detonation)
    private const float FieldEffectInterval = 80f;
    private const int MaxFieldParticles = 16;

    // Lightning burst visual
    private const int LightningBolts = 12;
    private const float LightningDuration = 6000f; // Duration of the lightning burst visual - matches electrocution

    // State
    private bool _rising;        // Rising phase — before the burst
    private bool _detonated;      // Electric burst active
    private bool _electricActive; // True once the rise is complete and electrocution begins
    private float _lightningTimer;

    // Pre-detonation rise and rotate
    private float _riseOffset;
    private const float RiseTarget = 16f; // Approximately player height
    private const float RiseSpeed = 0.08f; // Units per ms (rises in ~200ms)
    private float _rotationAngle;
    private const float RotationSpeed = 0.006f; // Radians per ms

    // Dynamic bolt add/remove timer
    private float _boltShuffleTimer;
    private const float BoltShuffleInterval = 200f; // Shuffle bolts every 200ms

    // Radius check for entering players
    private float _radiusCheckTimer;
    private const float RadiusCheckInterval = 250f; // Check every 250ms

    // Electric field particles (idle aura)
    private readonly FieldParticle[] _fieldParticles = new FieldParticle[MaxFieldParticles];
    private int _fieldParticleIndex;
    private float _fieldEffectTimer;

    // Lightning bolt visuals (on detonation)
    private readonly LightningBolt[] _lightningBolts = new LightningBolt[LightningBolts];

    // Procedural glow texture
    private static Texture2D _glowTexture;
    private static readonly object _textureLock = new();

    internal ObjectShowStopperThrown(ObjectDataStartParams startParams) : base(startParams) => ExplosionTimer = 3000f;

    private static Texture2D CreateGlowTexture(GraphicsDevice graphicsDevice)
    {
        const int size = 32;
        Color[] data = new Color[size * size];
        float center = size / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy) / center;
                float alpha = Math.Max(0f, 1f - dist);
                alpha *= alpha;
                byte a = (byte)(alpha * 255);
                data[y * size + x] = new Color(a, a, a, a);
            }
        }

        Texture2D texture = new(graphicsDevice, size, size);
        texture.SetData(data);
        return texture;
    }

    public override void Initialize()
    {
        EnableUpdateObject();
        GameWorld.PortalsObjectsToKeepTrackOf.Add(this);
        Body.SetAngularDamping(3f);
        Body.SetBullet(true);

        if (_glowTexture == null || _glowTexture.IsDisposed)
        {
            lock (_textureLock)
            {
                if (_glowTexture == null || _glowTexture.IsDisposed)
                {
                    _glowTexture = CreateGlowTexture(Constants.WhitePixel.GraphicsDevice);
                }
            }
        }

        for (int i = 0; i < MaxFieldParticles; i++)
        {
            _fieldParticles[i] = new FieldParticle();
        }

        for (int i = 0; i < LightningBolts; i++)
        {
            _lightningBolts[i] = new LightningBolt();
        }
    }

    public override void OnRemoveObject() => GameWorld.PortalsObjectsToKeepTrackOf.Remove(this);

    /// <summary>
    /// Returns the world position offset upward by the current rise during detonation.
    /// </summary>
    private Vector2 GetRaisedPosition()
    {
        Vector2 pos = GetWorldPosition();
        pos.Y += _riseOffset; // World Y is up in SFD
        return pos;
    }

    public override void MissileHitPlayer(Player player, MissileHitEventArgs e)
    {
        if (!_rising && !_detonated)
        {
            base.MissileHitPlayer(player, e);
            Body.SetLinearVelocity(Body.GetLinearVelocity() * new Vector2(0.2f, 1f));
            Body.SetAngularVelocity(Body.GetAngularVelocity() * 0.2f);
        }
    }

    public override void BeforePlayerMeleeHit(Player player, PlayerBeforeHitEventArgs e)
    {
        if (m_timeBeforeEnablePlayerHit > 0f || _rising || _detonated)
        {
            e.Cancel = true;
        }
    }

    public override void PlayerMeleeHit(Player player, PlayerHitEventArgs e)
    {
        if (!_rising && !_detonated)
        {
            ObjectDataMethods.DefaultPlayerHitBaseballEffect(this, player, e);
        }
    }

    public override void ExplosionHit(Explosion explosionData, ExplosionHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && explosionData.SourceExplosionDamage > 0f)
        {
            if (!_rising && !_detonated)
            {
                StartRising();
            }
            else if (_detonated)
            {
                Destroy();
            }
        }
    }

    public override void ProjectileHit(Projectile projectile, ProjectileHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && projectile.Properties.ProjectileID != 64)
        {
            if (!_rising && !_detonated)
            {
                StartRising();
            }
            else if (_detonated)
            {
                Destroy();
            }
        }
    }

    public override void SetProperties() => Properties.Add(ObjectPropertyID.Grenade_DudChance);

    /// <summary>
    /// Starts the rising phase — grenade floats up and rotates before the electric burst.
    /// </summary>
    private void StartRising()
    {
        if (_rising || _detonated) return;

        _rising = true;

        // Stop the grenade physics
        Body.SetLinearVelocity(Vector2.Zero);
        Body.SetAngularVelocity(0f);
        ChangeBodyType(BodyType.Static);

        Filter filter = new()
        {
            categoryBits = 0,
            aboveBits = 0,
            maskBits = 0,
            blockMelee = false,
            projectileHit = false,
            absorbProjectile = false
        };
        Body.GetFixtureByIndex(0).SetFilterData(ref filter);

        // Play a charging/rising sound
        if (GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("ElectroHit", GetWorldPosition(), 0.4f, GameWorld);
        }
    }

    private void TriggerLightningBurst()
    {
        if (_detonated) return;

        _detonated = true;
    }

    /// <summary>
    /// Called once the grenade has finished rising. Triggers the actual electric burst.
    /// </summary>
    private void ActivateElectricField()
    {
        _electricActive = true;
        _lightningTimer = LightningDuration;

        Vector2 position = GetRaisedPosition();

        // Electrocute players in range (server-side only)
        if (GameOwner != GameOwnerEnum.Client)
        {
            AABB.Create(out AABB area, position, position, BurstRadius);
            foreach (ObjectData obj in GameWorld.GetObjectDataByArea(area, false, SFDGameScriptInterface.PhysicsLayer.Active))
            {
                if (obj.InternalData is Player player && !player.IsDead && !player.IsRemoved)
                {
                    float distance = Vector2.Distance(player.Position, position);
                    if (distance <= BurstRadius)
                    {
                        ElectrocutePlayer(player);
                    }
                }
            }
        }

        // Visual and sound effects
        if (GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("ElectroHit", position, GameWorld);

            // Camera shake
            EffectHandler.PlayEffect("CAM_S", Vector2.Zero, GameWorld, 1.5f, 200f, false);

            // Spark effects
            for (int i = 0; i < 8; i++)
            {
                Vector2 sparkPos = position + new Vector2(
                    Globals.Random.NextFloat(-10f, 10f),
                    Globals.Random.NextFloat(-10f, 10f));
                EffectHandler.PlayEffect("S_P", sparkPos, GameWorld);
            }

            // Generate lightning bolts from the raised position
            GenerateLightningBolts(position);
        }
    }

    private void ElectrocutePlayer(Player player, float duration = ElectrocutionDuration)
    {
        ExtendedPlayer extendedPlayer = player.GetExtension();
        // Don't re-electrocute if already electrocuted with more time remaining
        if (extendedPlayer.Electrocuted && extendedPlayer.Time.Electrocution >= duration)
            return;

        extendedPlayer.Electrocuted = true;
        extendedPlayer.Time.Electrocution = duration;

        // Set player to ReadOnly input so they can't move
        player.SetInputMode(SFDGameScriptInterface.PlayerInputMode.ReadOnly);

        // Sync the state to clients
        Sync.Generic.GenericData.SendGenericDataToClients(
            new Sync.Generic.GenericData(Sync.Generic.DataType.ExtraClientStates, [], player.ObjectID, extendedPlayer.GetStates()));
    }

    private void GenerateLightningBolts(Vector2 center)
    {
        for (int i = 0; i < LightningBolts; i++)
        {
            float angle = (float)(Math.PI * 2 * i / LightningBolts);
            float length = Globals.Random.NextFloat(BurstRadius * 0.5f, BurstRadius);
            Vector2 end = center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * length;
            _lightningBolts[i].Activate(center, end, LightningDuration);
        }
    }

    public override void UpdateObject(float ms)
    {
        m_timeBeforeEnablePlayerHit -= ms;

        if (_detonated)
        {
            _rotationAngle += RotationSpeed * ms;

            // Nothing else happens until the electric field is active
            if (!_electricActive)
            {
                return;
            }

            _lightningTimer -= ms;

            // Check for players entering the radius (server-side)
            if (GameOwner != GameOwnerEnum.Client && _lightningTimer > 0f)
            {
                _radiusCheckTimer -= ms;
                if (_radiusCheckTimer <= 0f)
                {
                    _radiusCheckTimer = RadiusCheckInterval;
                    Vector2 center = GetRaisedPosition();
                    AABB.Create(out AABB area, center, center, BurstRadius);
                    foreach (ObjectData obj in GameWorld.GetObjectDataByArea(area, false, SFDGameScriptInterface.PhysicsLayer.Active))
                    {
                        if (obj.InternalData is Player player && !player.IsDead && !player.IsRemoved)
                        {
                            float distance = Vector2.Distance(player.Position, center);
                            if (distance <= BurstRadius)
                            {
                                // Shock for the remaining duration
                                ElectrocutePlayer(player, _lightningTimer);
                            }
                        }
                    }
                }
            }

            // Update lightning bolt visuals
            if (GameOwner != GameOwnerEnum.Server)
            {
                Vector2 center = GetRaisedPosition();

                // Randomly add/remove bolts for dynamic electric effect
                _boltShuffleTimer -= ms;
                if (_boltShuffleTimer <= 0f && _lightningTimer > 500f)
                {
                    _boltShuffleTimer = BoltShuffleInterval;

                    // Randomly deactivate 1-3 bolts
                    int toRemove = Globals.Random.Next(1, 4);
                    for (int r = 0; r < toRemove; r++)
                    {
                        int idx = Globals.Random.Next(0, LightningBolts);
                        _lightningBolts[idx].Active = false;
                    }

                    // Randomly spawn 1-4 new bolts
                    int toAdd = Globals.Random.Next(1, 5);
                    for (int a = 0; a < toAdd; a++)
                    {
                        int idx = Globals.Random.Next(0, LightningBolts);
                        if (!_lightningBolts[idx].Active)
                        {
                            float angle = (float)(Globals.Random.NextDouble() * Math.PI * 2);
                            float length = Globals.Random.NextFloat(BurstRadius * 0.3f, BurstRadius);
                            Vector2 end = center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * length;
                            _lightningBolts[idx].Activate(center, end, Globals.Random.NextFloat(200f, 600f));
                        }
                    }
                }

                for (int i = 0; i < LightningBolts; i++)
                {
                    if (_lightningBolts[i].Active)
                    {
                        _lightningBolts[i].Update(ms);
                    }
                    else if (_lightningTimer > 500f)
                    {
                        // Respawn expired bolts to keep the lightning storm going
                        float angle = (float)(Globals.Random.NextDouble() * Math.PI * 2);
                        float length = Globals.Random.NextFloat(BurstRadius * 0.3f, BurstRadius);
                        Vector2 end = center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * length;
                        _lightningBolts[i].Activate(center, end, Globals.Random.NextFloat(300f, 800f));
                    }
                }

                // Play periodic zap sounds
                if (_lightningTimer % 800f < ms)
                {
                    SoundHandler.PlaySound("ElectroHit", center, 0.5f, GameWorld);
                }
            }

            if (_lightningTimer <= 0f)
            {
                if (GameOwner != GameOwnerEnum.Client)
                {
                    Destroy();
                }
            }

            return;
        }

        // Rising phase — grenade floats up before detonation
        if (_rising)
        {
            _riseOffset += RiseSpeed * ms;
            _rotationAngle += RotationSpeed * ms;

            if (_riseOffset >= RiseTarget)
            {
                _riseOffset = RiseTarget;
                TriggerLightningBurst();
                ActivateElectricField();
            }
            return;
        }

        // Pre-detonation: electric field effect
        if (GameOwner != GameOwnerEnum.Server)
        {
            _fieldEffectTimer -= ms;
            if (_fieldEffectTimer <= 0f)
            {
                _fieldEffectTimer = FieldEffectInterval;
                SpawnFieldParticle();
            }

            for (int i = 0; i < MaxFieldParticles; i++)
            {
                if (_fieldParticles[i].Active)
                {
                    _fieldParticles[i].Update(ms);
                }
            }
        }

        if (GameOwner != GameOwnerEnum.Client)
        {
            ExplosionTimer -= ms;
            if (ExplosionTimer <= 0f)
            {
                m_timeBeforeEnablePlayerHit = 0f;

                if (Globals.Random.NextFloat() < GetDudChance())
                {
                    DisableUpdateObject();
                    EffectHandler.PlayEffect("GR_D", GetWorldPosition(), GameWorld);
                    SoundHandler.PlaySound("GrenadeDud", GameWorld);
                    ExplosionResultedInDud = true;
                    return;
                }

                StartRising();
            }
        }
    }

    private void SpawnFieldParticle()
    {
        Vector2 grenadePos = GetWorldPosition();
        float angle = (float)(Globals.Random.NextDouble() * Math.PI * 2);
        float radius = Globals.Random.NextFloat(4f, 10f);
        Vector2 offset = new((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius);

        _fieldParticles[_fieldParticleIndex].Activate(
            grenadePos + offset,
            new Vector2(Globals.Random.NextFloat(-0.3f, 0.3f), Globals.Random.NextFloat(0.1f, 0.4f)),
            Globals.Random.NextFloat(200f, 500f),
            Globals.Random.NextFloat(0.3f, 0.8f)
        );

        _fieldParticleIndex = (_fieldParticleIndex + 1) % MaxFieldParticles;
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Server)
        {
            Vector2 pos = GetWorldPosition();
            for (int i = 0; i < 6; i++)
            {
                Vector2 sparkPos = pos + new Vector2(
                    Globals.Random.NextFloat(-8f, 8f),
                    Globals.Random.NextFloat(-8f, 8f));
                EffectHandler.PlayEffect("S_P", sparkPos, GameWorld);
            }
        }
    }

    public override void Draw(SpriteBatch spriteBatch, float ms)
    {
        // Draw the grenade object itself
        foreach (ObjectDecal objectDecal in m_objectDecals)
        {
            Vector2 position = objectDecal.HaveOffset ? Body.GetWorldPoint(objectDecal.LocalOffset) : Body.Position;
            // Apply rise offset during rising and after detonation
            if (_rising || _detonated)
            {
                position.Y += Converter.ConvertWorldToBox2D(_riseOffset);
            }
            Camera.ConvertBox2DToScreen(ref position, out position);
            float rotation = (_rising || _detonated) ? _rotationAngle : -Body.GetAngle();
            spriteBatch.Draw(objectDecal.Texture, position, null, Color.Gray, rotation, objectDecal.TextureOrigin, Camera.Zoom, m_faceDirectionSpriteEffect, 0f);
        }

        if (_glowTexture == null || _glowTexture.IsDisposed) return;

        Vector2 texOrigin = new(_glowTexture.Width * 0.5f, _glowTexture.Height * 0.5f);

        // Draw electric field particles (pre-detonation aura)
        if (!_detonated)
        {
            for (int i = 0; i < MaxFieldParticles; i++)
            {
                if (!_fieldParticles[i].Active) continue;

                FieldParticle p = _fieldParticles[i];
                Vector2 screenPos = p.Position;
                Camera.ConvertWorldToScreen(ref screenPos, out screenPos);

                float alpha = p.Alpha;
                if (alpha <= 0f) continue;

                // Electric cyan/blue glow
                int a = Math.Min(255, (int)(alpha * 180));
                if (a > 0)
                {
                    Color outerColor = new(80, 160, 255, a);
                    spriteBatch.Draw(_glowTexture, screenPos, null, outerColor, 0f, texOrigin, p.Scale * Camera.Zoom, SpriteEffects.None, 0f);

                    // Brighter white-blue core
                    int a2 = Math.Min(255, (int)(alpha * 120));
                    Color coreColor = new(200, 230, 255, a2);
                    spriteBatch.Draw(_glowTexture, screenPos, null, coreColor, 0f, texOrigin, p.Scale * 0.4f * Camera.Zoom, SpriteEffects.None, 0f);
                }
            }
        }

        // Draw lightning bolts (on detonation)
        if (_detonated)
        {
            float globalAlpha = _lightningTimer < 1000f ? _lightningTimer / 1000f : 1f;
            for (int i = 0; i < LightningBolts; i++)
            {
                if (!_lightningBolts[i].Active) continue;

                LightningBolt bolt = _lightningBolts[i];
                DrawLightningBolt(spriteBatch, bolt, globalAlpha);
            }

            // Central flash — pulses while active, fades in last 500ms
            float fadeOut = _lightningTimer < 500f ? _lightningTimer / 500f : 1f;
            if (fadeOut > 0.05f)
            {
                Vector2 center = GetRaisedPosition();
                Camera.ConvertWorldToScreen(ref center, out Vector2 screenCenter);

                // Pulsing glow
                float pulse = 0.5f + 0.5f * (float)Math.Sin(_lightningTimer * 0.008f);
                int fa = Math.Min(255, (int)(fadeOut * pulse * 160));
                Color flashColor = new(120, 180, 255, fa);
                spriteBatch.Draw(_glowTexture, screenCenter, null, flashColor, 0f, texOrigin, 3f * Camera.Zoom, SpriteEffects.None, 0f);
            }
        }
    }

    private void DrawLightningBolt(SpriteBatch spriteBatch, LightningBolt bolt, float globalAlpha)
    {
        if (bolt.Segments == null) return;

        for (int i = 0; i < bolt.Segments.Length - 1; i++)
        {
            Vector2 startWorld = bolt.Segments[i];
            Vector2 endWorld = bolt.Segments[i + 1];

            Camera.ConvertWorldToScreen(ref startWorld, out Vector2 startScreen);
            Camera.ConvertWorldToScreen(ref endWorld, out Vector2 endScreen);

            Vector2 diff = endScreen - startScreen;
            float length = diff.Length();
            if (length < 0.5f) continue;

            float angle = (float)Math.Atan2(diff.Y, diff.X);

            float segAlpha = globalAlpha * bolt.Alpha;
            int a = Math.Min(255, (int)(segAlpha * 255));
            if (a <= 0) continue;

            // Outer glow (cyan)
            Color outerColor = new(60, 180, 255, (int)(a * 0.5f));
            spriteBatch.Draw(Constants.WhitePixel, startScreen, null, outerColor, angle, Vector2.Zero,
                new Vector2(length, 3f * Camera.Zoom), SpriteEffects.None, 0f);

            // Core (bright white-blue)
            Color coreColor = new(220, 240, 255, a);
            spriteBatch.Draw(Constants.WhitePixel, startScreen, null, coreColor, angle, Vector2.Zero,
                new Vector2(length, 1.5f * Camera.Zoom), SpriteEffects.None, 0f);
        }
    }

    public override void ImpactHit(ObjectData otherObject, ImpactHitEventArgs e)
    {
        if (!_detonated)
        {
            base.ImpactHit(otherObject, e);
            if (GameOwner != GameOwnerEnum.Server)
            {
                SoundHandler.PlaySound(Tile.ImpactSound, GetWorldPosition(), GameWorld);
                EffectHandler.PlayEffect(Tile.ImpactEffect, GetWorldPosition(), GameWorld);
            }
        }
    }

    /// <summary>
    /// A small electric spark particle for the idle field effect.
    /// </summary>
    private class FieldParticle
    {
        internal bool Active;
        internal Vector2 Position;
        internal Vector2 Velocity;
        internal float Lifetime;
        internal float MaxLifetime;
        internal float Scale;
        internal float Alpha;

        internal void Activate(Vector2 position, Vector2 velocity, float lifetime, float scale)
        {
            Active = true;
            Position = position;
            Velocity = velocity;
            Lifetime = lifetime;
            MaxLifetime = lifetime;
            Scale = scale;
            Alpha = 0f;
        }

        internal void Update(float ms)
        {
            Lifetime -= ms;
            if (Lifetime <= 0f)
            {
                Active = false;
                return;
            }

            float t = Lifetime / MaxLifetime;

            // Quick flash in, fade out
            if (t > 0.7f)
            {
                Alpha = (1f - t) / 0.3f;
            }
            else
            {
                Alpha = t / 0.7f;
            }

            Position += Velocity * (ms / 16f);
        }
    }

    /// <summary>
    /// A lightning bolt segment for the burst effect.
    /// </summary>
    private class LightningBolt
    {
        internal bool Active;
        internal Vector2[] Segments;
        internal float Lifetime;
        internal float MaxLifetime;
        internal float Alpha;
        private float _jitterTimer;

        internal void Activate(Vector2 start, Vector2 end, float lifetime)
        {
            Active = true;
            Lifetime = lifetime;
            MaxLifetime = lifetime;
            Alpha = 1f;
            GenerateSegments(start, end);
        }

        private void GenerateSegments(Vector2 start, Vector2 end)
        {
            const int segmentCount = 8;
            Segments = new Vector2[segmentCount + 1];
            Segments[0] = start;
            Segments[segmentCount] = end;

            for (int i = 1; i < segmentCount; i++)
            {
                float t = (float)i / segmentCount;
                Vector2 midpoint = Vector2.Lerp(start, end, t);

                // Perpendicular offset for jaggedness
                Vector2 dir = end - start;
                Vector2 perp = new(-dir.Y, dir.X);
                if (perp.LengthSquared() > 0)
                {
                    perp.Normalize();
                }

                float offset = Globals.Random.NextFloat(-6f, 6f) * (1f - Math.Abs(t - 0.5f) * 2f);
                Segments[i] = midpoint + perp * offset;
            }
        }

        internal void Update(float ms)
        {
            Lifetime -= ms;
            if (Lifetime <= 0f)
            {
                Active = false;
                return;
            }

            Alpha = Lifetime / MaxLifetime;

            // Re-jitter segments periodically for a flickering effect
            _jitterTimer -= ms;
            if (_jitterTimer <= 0f)
            {
                _jitterTimer = 50f;
                if (Segments != null && Segments.Length > 2)
                {
                    Vector2 start = Segments[0];
                    Vector2 end = Segments[Segments.Length - 1];
                    GenerateSegments(start, end);
                }
            }
        }
    }
}
