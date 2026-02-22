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
using SFR.Helper;
using SFR.Misc;
using Explosion = SFD.Explosion;
using Player = SFD.Player;

namespace SFR.Objects;

internal sealed class ObjectGasGrenadeThrown : ObjectGrenadeThrown
{
    // Gas cloud parameters
    private const float GasDuration = 30000f; // 30 seconds in ms
    private const float GasRadius = 36f; // Radius of gas cloud in world units
    private const float DamagePerTick = 3.75f; // Damage per tick
    private const float DamageInterval = 500f; // Damage every 500ms
    private const float EffectInterval = 150f; // Spawn puff particles
    private const float SmokeEffectInterval = 400f; // Spawn built-in smoke effects
    private const int MaxParticles = 12; // One big central cloud

    // Gas cloud state
    private bool _gasActive;
    private float _gasTimer;
    private float _damageTickTimer;
    private float _effectTickTimer;
    private float _smokeEffectTimer;
    private float _gasSoundTimer;
    private Vector2 _gasPosition;

    // Procedurally generated soft circular smoke puff texture
    private static Texture2D _smokePuffTexture;
    private static readonly object _textureLock = new();

    // Particle system for green gas puffs
    private readonly GasParticle[] _particles = new GasParticle[MaxParticles];
    private int _particleIndex;

    internal ObjectGasGrenadeThrown(ObjectDataStartParams startParams) : base(startParams) => ExplosionTimer = 3000f;

    /// <summary>
    /// Create a soft circular gradient texture for smoke puffs.
    /// This generates a 64x64 white circle that fades smoothly from center to edges.
    /// When drawn with a green Color tint, it looks like green smoke.
    /// </summary>
    private static Texture2D CreateSmokePuffTexture(GraphicsDevice graphicsDevice)
    {
        const int size = 64;
        Color[] data = new Color[size * size];
        float center = size / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy) / center;

                // Smooth circular falloff — dense center, soft edges
                float alpha = Math.Max(0f, 1f - dist);
                alpha *= alpha; // Quadratic falloff for softer edges

                byte a = (byte)(alpha * 255);
                data[y * size + x] = new Color(a, a, a, a); // Premultiplied alpha white
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

        // Generate the smoke puff texture once (shared across all gas grenades)
        if (_smokePuffTexture == null || _smokePuffTexture.IsDisposed)
        {
            lock (_textureLock)
            {
                if (_smokePuffTexture == null || _smokePuffTexture.IsDisposed)
                {
                    _smokePuffTexture = CreateSmokePuffTexture(Constants.WhitePixel.GraphicsDevice);
                }
            }
        }

        for (int i = 0; i < MaxParticles; i++)
        {
            _particles[i] = new GasParticle();
        }
    }

    public override void OnRemoveObject() => GameWorld.PortalsObjectsToKeepTrackOf.Remove(this);

    public override void MissileHitPlayer(Player player, MissileHitEventArgs e)
    {
        if (!_gasActive)
        {
            base.MissileHitPlayer(player, e);
            Body.SetLinearVelocity(Body.GetLinearVelocity() * new Vector2(0.2f, 1f));
            Body.SetAngularVelocity(Body.GetAngularVelocity() * 0.2f);
        }
    }

    public override void BeforePlayerMeleeHit(Player player, PlayerBeforeHitEventArgs e)
    {
        if (m_timeBeforeEnablePlayerHit > 0f || _gasActive)
        {
            e.Cancel = true;
        }
    }

    public override void PlayerMeleeHit(Player player, PlayerHitEventArgs e)
    {
        if (!_gasActive)
        {
            ObjectDataMethods.DefaultPlayerHitBaseballEffect(this, player, e);
        }
    }

    public override void ExplosionHit(Explosion explosionData, ExplosionHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && explosionData.SourceExplosionDamage > 0f)
        {
            if (_gasActive)
            {
                Destroy();
            }
            else
            {
                ActivateGasCloud();
            }
        }
    }

    public override void ProjectileHit(Projectile projectile, ProjectileHitEventArgs e)
    {
        if (GameOwner != GameOwnerEnum.Client && projectile.Properties.ProjectileID != 64)
        {
            if (_gasActive)
            {
                Destroy();
            }
            else
            {
                ActivateGasCloud();
            }
        }
    }

    public override void SetProperties() => Properties.Add(ObjectPropertyID.Grenade_DudChance);

    private void ActivateGasCloud()
    {
        if (_gasActive) return;

        _gasActive = true;
        _gasTimer = GasDuration;
        _damageTickTimer = 0f;
        _effectTickTimer = 0f;
        _smokeEffectTimer = 0f;
        _gasSoundTimer = 0f;
        _gasPosition = GetWorldPosition();

        // Stop the grenade from moving
        Body.SetLinearVelocity(Vector2.Zero);
        Body.SetAngularVelocity(0f);
        ChangeBodyType(BodyType.Static);

        // Keep projectileHit enabled so the grenade can be shot to destroy it
        Filter filter = new()
        {
            categoryBits = 0,
            aboveBits = 0,
            maskBits = 0,
            blockMelee = false,
            projectileHit = true,
            absorbProjectile = false
        };
        Body.GetFixtureByIndex(0).SetFilterData(ref filter);

        if (GameOwner != GameOwnerEnum.Server)
        {
            SoundHandler.PlaySound("ImpactDefault", _gasPosition, GameWorld);

            // Seed the cloud with initial puff particles
            for (int i = 0; i < 5; i++)
            {
                SpawnGasParticle(true);
            }
        }
    }

    public override void UpdateObject(float ms)
    {
        m_timeBeforeEnablePlayerHit -= ms;

        if (_gasActive)
        {
            UpdateGasCloud(ms);
            return;
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

                ActivateGasCloud();
            }
        }
    }

    private void UpdateGasCloud(float ms)
    {
        _gasTimer -= ms;

        if (_gasTimer <= 0f)
        {
            if (GameOwner != GameOwnerEnum.Client)
            {
                Destroy();
            }

            return;
        }

        // Deal damage
        if (GameOwner != GameOwnerEnum.Client)
        {
            _damageTickTimer -= ms;
            if (_damageTickTimer <= 0f)
            {
                _damageTickTimer = DamageInterval;
                DealGasDamage();
            }
        }

        // Spawn green puff particles and built-in smoke effects
        if (GameOwner != GameOwnerEnum.Server)
        {
            _effectTickTimer -= ms;
            if (_effectTickTimer <= 0f)
            {
                _effectTickTimer = EffectInterval;
                SpawnGasParticle(false);
            }

            // Also periodically spawn built-in TR_S smoke effects for extra visual depth
            _smokeEffectTimer -= ms;
            if (_smokeEffectTimer <= 0f)
            {
                _smokeEffectTimer = SmokeEffectInterval;
                Vector2 smokePos = _gasPosition + new Vector2(
                    Globals.Random.NextFloat(-GasRadius * 0.5f, GasRadius * 0.5f),
                    Globals.Random.NextFloat(-GasRadius * 0.4f, GasRadius * 0.4f));
                EffectHandler.PlayEffect("TR_S", smokePos, GameWorld);
            }

            // Gas release hiss sound
            _gasSoundTimer -= ms;
            if (_gasSoundTimer <= 0f)
            {
                _gasSoundTimer = 1200f;
                SoundHandler.PlaySound("GasHiss", _gasPosition, 0.6f, GameWorld);
            }

            // Update existing particles
            for (int i = 0; i < MaxParticles; i++)
            {
                if (_particles[i].Active)
                {
                    _particles[i].Update(ms);
                }
            }
        }
    }

    private void DealGasDamage()
    {
        AABB.Create(out AABB area, _gasPosition, _gasPosition, GasRadius);
        foreach (ObjectData obj in GameWorld.GetObjectDataByArea(area, false, SFDGameScriptInterface.PhysicsLayer.Active))
        {
            if (obj.InternalData is Player player && !player.IsDead && !player.IsRemoved)
            {
                float distance = Vector2.Distance(player.Position, _gasPosition);
                if (distance <= GasRadius)
                {
                    player.TakeMiscDamage(DamagePerTick, sourceID: ObjectID);
                }
            }
        }
    }

    private void SpawnGasParticle(bool initial)
    {
        float angle = (float)(Globals.Random.NextDouble() * Math.PI * 2);
        float spawnRadius = initial
            ? (float)(Globals.Random.NextDouble() * GasRadius * 0.15f)
            : (float)(Globals.Random.NextDouble() * GasRadius * 0.1f);
        Vector2 spawnOffset = new((float)Math.Cos(angle) * spawnRadius, (float)Math.Sin(angle) * spawnRadius);

        Vector2 driftDir = spawnRadius > 1f ? Vector2.Normalize(spawnOffset) : new Vector2(Globals.Random.NextFloat(-1f, 1f), Globals.Random.NextFloat(-1f, 1f));
        float driftSpeed = Globals.Random.NextFloat(0.08f, 0.25f);
        Vector2 velocity = driftDir * driftSpeed + new Vector2(0f, Globals.Random.NextFloat(0.02f, 0.1f));

        // Large overlapping puffs for a thick cloudy look
        float scale = Globals.Random.NextFloat(2.5f, 4.5f);

        _particles[_particleIndex].Activate(
            _gasPosition + spawnOffset,
            velocity,
            Globals.Random.NextFloat(2500f, 5000f),
            scale
        );

        _particleIndex = (_particleIndex + 1) % MaxParticles;
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Server)
        {
            // Burst of smoke effects on destruction
            for (int i = 0; i < 6; i++)
            {
                Vector2 pos = GetWorldPosition() + new Vector2(
                    Globals.Random.NextFloat(-12f, 12f),
                    Globals.Random.NextFloat(-12f, 12f));
                EffectHandler.PlayEffect("TR_S", pos, GameWorld);
            }
        }
    }

    public override void Draw(SpriteBatch spriteBatch, float ms)
    {
        // Always draw the grenade canister
        foreach (ObjectDecal objectDecal in m_objectDecals)
        {
            Vector2 position = objectDecal.HaveOffset ? Body.GetWorldPoint(objectDecal.LocalOffset) : Body.Position;
            Camera.ConvertBox2DToScreen(ref position, out position);
            float rotation = -Body.GetAngle();
            spriteBatch.Draw(objectDecal.Texture, position, null, Color.Gray, rotation, objectDecal.TextureOrigin, Camera.Zoom, m_faceDirectionSpriteEffect, 0f);
        }

        // Draw green gas cloud using the procedural soft smoke puff texture
        if (_gasActive && _smokePuffTexture != null && !_smokePuffTexture.IsDisposed)
        {
            float globalAlpha = _gasTimer < 3000f ? _gasTimer / 3000f : 1f;
            Vector2 texOrigin = new(_smokePuffTexture.Width * 0.5f, _smokePuffTexture.Height * 0.5f);

            for (int i = 0; i < MaxParticles; i++)
            {
                if (!_particles[i].Active) continue;

                GasParticle p = _particles[i];
                Vector2 screenPos = p.Position;
                Camera.ConvertWorldToScreen(ref screenPos, out screenPos);

                float alpha = p.Alpha * globalAlpha;
                if (alpha <= 0f) continue;

                float scale = p.Scale * Camera.Zoom;

                // Outer green puff — semi-transparent for layered blending
                int a1 = Math.Min(255, (int)(alpha * 80));
                if (a1 > 0)
                {
                    Color outerColor = new(15, 80, 15, a1);
                    spriteBatch.Draw(_smokePuffTexture, screenPos, null, outerColor, p.Rotation, texOrigin, scale, SpriteEffects.None, 0f);
                }

                // Inner brighter core
                int a2 = Math.Min(255, (int)(alpha * 55));
                if (a2 > 0)
                {
                    Color innerColor = new(25, 110, 25, a2);
                    spriteBatch.Draw(_smokePuffTexture, screenPos, null, innerColor, p.Rotation + 0.5f, texOrigin, scale * 0.6f, SpriteEffects.None, 0f);
                }
            }
        }
    }

    public override void ImpactHit(ObjectData otherObject, ImpactHitEventArgs e)
    {
        if (!_gasActive)
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
    /// A single gas cloud puff particle.
    /// </summary>
    private class GasParticle
    {
        internal bool Active;
        internal Vector2 Position;
        internal Vector2 Velocity;
        internal float Lifetime;
        internal float MaxLifetime;
        internal float Scale;
        internal float Rotation;
        internal float Alpha;

        internal void Activate(Vector2 position, Vector2 velocity, float lifetime, float scale)
        {
            Active = true;
            Position = position;
            Velocity = velocity;
            Lifetime = lifetime;
            MaxLifetime = lifetime;
            Scale = scale;
            Rotation = Globals.Random.NextFloat(0f, (float)(Math.PI * 2));
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

            float t = Lifetime / MaxLifetime; // 1 = just spawned, 0 = about to die

            // Fade in quickly, hold, then fade out
            if (t > 0.85f)
            {
                Alpha = (1f - t) / 0.15f;
            }
            else if (t < 0.3f)
            {
                Alpha = t / 0.3f;
            }
            else
            {
                Alpha = 1f;
            }

            // Slowly expand
            Scale += ms * 0.00003f;

            // Drift slowly
            Position += Velocity * (ms / 16f);

            // Slow rotation for organic turbulence
            Rotation += ms * 0.0002f;
        }
    }
}
