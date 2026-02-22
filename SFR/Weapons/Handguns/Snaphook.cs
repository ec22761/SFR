using System;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Materials;
using SFD.Objects;
using SFD.Sounds;
using SFD.Weapons;
using SFR.Helper;
using SFR.Misc;
using Player = SFD.Player;

namespace SFR.Weapons.Handguns;

/// <summary>
/// Snaphook — a pistol that shoots a rope. Where the rope hits, the player is pulled towards it.
/// A rope line is drawn from the player to the hook point with a sine-wave animation for a rope effect.
/// </summary>
internal sealed class Snaphook : RWeapon, IExtendedWeapon
{
    private bool _hooked;
    private Vector2 _hookTarget;
    private float _hookTimer;
    private float _ropeTime;
    private float _noProgressTimer;
    private float _bestDistanceToHook;
    private bool _retracting;
    private float _retractTimer;
    private Vector2 _retractStart;
    private const float HookDuration = 1400f; // ms to pull the player
    private const float RetractDuration = 150f;
    private const float FailedPullTimeout = 1000f;
    private const float PullSpeed = 12f;
    private const float ArrivalDistance = 12f;
    private const int RopeSegments = 28;
    private const float RopeAmplitude = 0.85f;
    private const float RopeFrequency = 1.5f;

    internal Snaphook()
    {
        RWeaponProperties weaponProperties = new(116, "Snaphook", "WpnSnaphook", false, WeaponCategory.Secondary)
        {
            MaxMagsInWeapon = 1,
            MaxRoundsInMag = 1,
            MaxCarriedSpareMags = 6,
            StartMags = 3,
            CooldownBeforePostAction = 400,
            CooldownAfterPostAction = 0,
            ExtraAutomaticCooldown = 200,
            ShellID = "",
            AccuracyDeflection = 0.02f,
            ProjectileID = -1,
            MuzzlePosition = new Vector2(8f, -2f),
            MuzzleEffectTextureID = "MuzzleFlashS",
            BlastSoundID = "",
            DrawSoundID = "MagnumDraw",
            GrabAmmoSoundID = "MagnumReload",
            OutOfAmmoSoundID = "OutOfAmmoLight",
            CursorAimOffset = new Vector2(0f, 3.5f),
            LazerPosition = new Vector2(8f, -0.5f),
            AimStartSoundID = "PistolAim",
            AI_DamageOutput = DamageOutputType.Low,
            CanRefilAtAmmoStashes = true,
            BreakDebris = ["ItemDebrisShiny00", "MetalDebris00C"],
            SpecialAmmoBulletsRefill = 3,
            VisualText = "Snaphook"
        };

        RWeaponVisuals weaponVisuals = new()
        {
            AnimIdleUpper = "UpperIdleHandgun",
            AnimCrouchUpper = "UpperCrouchHandgun",
            AnimJumpKickUpper = "UpperJumpKickHandgun",
            AnimJumpUpper = "UpperJumpHandgun",
            AnimJumpUpperFalling = "UpperJumpFallingHandgun",
            AnimKickUpper = "UpperKickHandgun",
            AnimStaggerUpper = "UpperStaggerHandgun",
            AnimRunUpper = "UpperRunHandgun",
            AnimWalkUpper = "UpperWalkHandgun",
            AnimUpperHipfire = "UpperHipfireHandgun",
            AnimFireArmLength = 7f,
            AnimDraw = "UpperDrawMagnum",
            AnimManualAim = "ManualAimHandgun",
            AnimManualAimStart = "ManualAimHandgunStart",
            AnimReloadUpper = "UpperReload",
            AnimFullLand = "FullLandHandgun",
            AnimToggleThrowingMode = "UpperToggleThrowing"
        };

        weaponVisuals.SetModelTexture("SnaphookM");
        weaponVisuals.SetDrawnTexture("SnaphookD");
        weaponVisuals.SetThrowingTexture("SnaphookThrowing");

        SetPropertiesAndVisuals(weaponProperties, weaponVisuals);
        CacheDrawnTextures(["Reload"]);
    }

    private Snaphook(RWeaponProperties weaponProperties, RWeaponVisuals weaponVisuals) => SetPropertiesAndVisuals(weaponProperties, weaponVisuals);

    public override RWeapon Copy()
    {
        Snaphook copy = new(Properties, Visuals);
        copy.CopyStatsFrom(this);
        return copy;
    }

    /// <summary>
    /// Instead of spawning a projectile, perform a raycast to find the hook target.
    /// </summary>
    public override void BeforeCreateProjectile(BeforeCreateProjectileArgs args)
    {
        args.Handled = true;
        args.FireResult = true;
        _retracting = false;
        _retractTimer = 0f;

        SoundHandler.PlaySound("BowShoot", args.Player.GameWorld);

        // Perform a raycast in the aim direction to find the hook point
        Vector2 origin = args.WorldPosition;
        Vector2 direction = args.Direction;
        float maxDistance = 400f;

        GameWorld.RayCastResult rayCastResult = args.GameWorld.RayCast(
            origin, direction, 0f, maxDistance,
            RopeRayCastCollision, _ => true);

        if (rayCastResult.EndFixture is not null)
        {
            _hooked = true;
            _hookTarget = rayCastResult.EndPosition;
            _hookTimer = HookDuration;
            _ropeTime = 0f;
            _noProgressTimer = FailedPullTimeout;
            _bestDistanceToHook = Vector2.Distance(args.Player.Position, _hookTarget);

            SoundHandler.PlaySound("MeleeBlock", args.Player.Position, args.Player.GameWorld);
        }
    }

    public void Update(Player player, float ms, float realMs)
    {
        if (_retracting)
        {
            _retractTimer -= ms;
            if (_retractTimer <= 0f)
            {
                _retracting = false;
            }
        }

        if (!_hooked) return;

        _hookTimer -= ms;
        _ropeTime += ms;

        Vector2 playerPos = player.Position;
        Vector2 toTarget = _hookTarget - playerPos;
        float distance = toTarget.Length();

        if (distance < _bestDistanceToHook - 0.25f)
        {
            _bestDistanceToHook = distance;
            _noProgressTimer = FailedPullTimeout;
        }
        else
        {
            _noProgressTimer -= ms;
            if (_noProgressTimer <= 0f)
            {
                StartRetract(player);
                return;
            }
        }

        // Release the hook if time is up or we've arrived
        if (_hookTimer <= 0f || distance < ArrivalDistance || player.IsDead || player.IsRemoved)
        {
            _hooked = false;
            return;
        }

        if (player.GameOwner != GameOwnerEnum.Client)
        {
            // Normalize direction towards hook point
            Vector2 pullDirection = toTarget;
            if (distance > 0f)
            {
                pullDirection.X /= distance;
                pullDirection.Y /= distance;
            }

            Vector2 velocity;

            // For steep vertical hooks while grounded, bias horizontal motion for a drag/swing feel,
            // but still keep enough vertical pull so downward shots actually move the player.
            if (player.StandingOnGround && Math.Abs(pullDirection.Y) > 0.55f)
            {
                float swingDir = Math.Abs(pullDirection.X) > 0.1f
                    ? Math.Sign(pullDirection.X)
                    : player.LastDirectionX;

                Vector2 blend = new(pullDirection.X + swingDir * 0.55f, pullDirection.Y);
                float blendLen = blend.Length();
                if (blendLen > 0f)
                {
                    blend /= blendLen;
                }

                velocity = blend * (PullSpeed * 1.15f);
            }
            else
            {
                // Normal pull: hook is at same level or above — pull straight toward it
                velocity = pullDirection * PullSpeed;
            }

            player.WorldBody.SetLinearVelocity(velocity);
            player.m_preBox2DLinearVelocity = velocity;
            player.AirControlBaseVelocity = velocity;
            player.ForceServerPositionState();
            player.ImportantUpdate = true;
        }
    }

    public void DrawExtra(SpriteBatch spriteBatch, Player player, float ms)
    {
        if (!_hooked && !_retracting) return;

        Vector2 startWorld = player.Position + new Vector2(0f, 16f);
        Vector2 endWorld;
        float alphaScale;
        float amplitudeScale;

        if (_hooked)
        {
            endWorld = _hookTarget;
            alphaScale = 1f;
            amplitudeScale = 1f;
        }
        else
        {
            float retractT = MathHelper.Clamp(1f - _retractTimer / RetractDuration, 0f, 1f);
            endWorld = Vector2.Lerp(_retractStart, startWorld, retractT);
            alphaScale = 1f - retractT;
            amplitudeScale = 0.3f * (1f - retractT);
        }

        Vector2 diff = endWorld - startWorld;
        float totalLength = diff.Length();
        if (totalLength < 1f) return;

        Vector2 forward = diff / totalLength;
        // Perpendicular vector for the sine wave offset
        Vector2 perp = new(-forward.Y, forward.X);

        // Animate a damped traveling wave for a cleaner rope-like oscillation
        float phase = _ropeTime * 0.010f;
        // Dampen amplitude as the player gets closer
        float dampen = Math.Min(1f, totalLength / 120f);

        for (int i = 0; i < RopeSegments; i++)
        {
            float t0 = i / (float)RopeSegments;
            float t1 = (i + 1) / (float)RopeSegments;

            // Sine offset, tapered at endpoints and damped over rope length
            float taper0 = (float)Math.Sin(t0 * Math.PI);
            float taper1 = (float)Math.Sin(t1 * Math.PI);
            float damp0 = (float)Math.Exp(-t0 * 0.7f);
            float damp1 = (float)Math.Exp(-t1 * 0.7f);
            float wave0 = (float)Math.Sin(t0 * RopeFrequency * Math.PI * 2 - phase) * RopeAmplitude * taper0 * damp0 * dampen * amplitudeScale;
            float wave1 = (float)Math.Sin(t1 * RopeFrequency * Math.PI * 2 - phase) * RopeAmplitude * taper1 * damp1 * dampen * amplitudeScale;
            wave0 += (float)Math.Sin(t0 * RopeFrequency * Math.PI * 4 - phase * 1.2f) * RopeAmplitude * 0.18f * taper0 * damp0 * dampen * amplitudeScale;
            wave1 += (float)Math.Sin(t1 * RopeFrequency * Math.PI * 4 - phase * 1.2f) * RopeAmplitude * 0.18f * taper1 * damp1 * dampen * amplitudeScale;

            Vector2 p0 = startWorld + forward * (t0 * totalLength) + perp * wave0;
            Vector2 p1 = startWorld + forward * (t1 * totalLength) + perp * wave1;

            Camera.ConvertWorldToScreen(ref p0, out Vector2 s0);
            Camera.ConvertWorldToScreen(ref p1, out Vector2 s1);

            Vector2 segDiff = s1 - s0;
            float segLength = segDiff.Length();
            if (segLength < 0.5f) continue;

            float angle = (float)Math.Atan2(segDiff.Y, segDiff.X);

            // Outer rope (brown)
            Color outerColor = new(132, 102, 74, (int)(150 * alphaScale));
            spriteBatch.Draw(Constants.WhitePixel, s0, null, outerColor, angle, Vector2.Zero,
                new Vector2(segLength, 1.35f * Camera.Zoom), SpriteEffects.None, 0f);

            // Inner rope (tan)
            Color innerColor = new(156, 124, 92, (int)(170 * alphaScale));
            spriteBatch.Draw(Constants.WhitePixel, s0, null, innerColor, angle, Vector2.Zero,
                new Vector2(segLength, 0.9f * Camera.Zoom), SpriteEffects.None, 0f);
        }
    }

    private void StartRetract(Player player)
    {
        _hooked = false;
        _retracting = true;
        _retractTimer = RetractDuration;
        _retractStart = _hookTarget;
        SoundHandler.PlaySound("Draw1", player.Position, player.GameWorld);
    }

    public void GetDealtDamage(Player player, float damage) { }
    public void OnHit(Player player, Player target) { }
    public void OnHitObject(Player player, PlayerHitEventArgs args, ObjectData obj) { }

    /// <summary>
    /// Raycast collision filter — hit solid objects and terrain, skip clouds and transparent materials.
    /// </summary>
    private static bool RopeRayCastCollision(Fixture fixture)
    {
        if (fixture.IsCloud()) return false;

        ObjectData objectData = ObjectData.Read(fixture);
        fixture.GetFilterData(out Filter filter);
        if ((filter.categoryBits & 15) > 0 || objectData.IsPlayer)
        {
            Material tileFixtureMaterial = objectData.Tile.GetTileFixtureMaterial(fixture.TileFixtureIndex);
            return !tileFixtureMaterial.Transparent;
        }

        return false;
    }

    public override void OnSubAnimationEvent(Player player, AnimationEvent animationEvent, AnimationData animationData, int currentFrameIndex)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animationEvent == AnimationEvent.EnterFrame && animationData.Name == "UpperDrawMagnum")
        {
            switch (currentFrameIndex)
            {
                case 1:
                    SoundHandler.PlaySound("Draw1", player.GameWorld);
                    break;
                case 6:
                    SoundHandler.PlaySound("MagnumDraw", player.GameWorld);
                    break;
            }
        }
    }

    public override void OnReloadAnimationEvent(Player player, AnimationEvent animEvent, SubAnimationPlayer subAnim)
    {
        if (player.GameOwner != GameOwnerEnum.Server && animEvent == AnimationEvent.EnterFrame)
        {
            if (subAnim.GetCurrentFrameIndex() == 1)
            {
                SoundHandler.PlaySound("MagnumReloadStart", player.Position, player.GameWorld);
            }
            if (subAnim.GetCurrentFrameIndex() == 4)
            {
                SoundHandler.PlaySound("MagnumReloadEnd", player.Position, player.GameWorld);
            }
        }
    }

    public override bool CheckDrawLazerAttachment(string subAnimation, int subFrame) => subAnimation is not "UpperReload";

    public override Texture2D GetDrawnTexture(ref GetDrawnTextureArgs args)
    {
        if (args.SubAnimation is "UpperReload" && args.SubFrame is >= 1 and <= 5)
        {
            args.Postfix = "Reload";
        }
        return base.GetDrawnTexture(ref args);
    }

    public override void OnThrowWeaponItem(Player player, ObjectWeaponItem thrownWeaponItem)
    {
        _hooked = false;
        _retracting = false;
        _retractTimer = 0f;
        Vector2 linearVelocity = thrownWeaponItem.Body.GetLinearVelocity();
        linearVelocity.X *= 1.1f;
        linearVelocity.Y *= 1f;
        thrownWeaponItem.Body.SetLinearVelocity(linearVelocity);
    }
}
