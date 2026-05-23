using System;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Sounds;
using SFR.Helper;
using SFR.Misc;
using SFR.Sync.Generic;

namespace SFR.Fighter.Jetpacks;

/// <summary>
/// All the jetpacks derive from this.
/// This class will handle basic tasks, such as setting basic speed, playing effects / sounds etc.
/// </summary>
internal abstract class GenericJetpack(float fuel = 100f, float maxSpeed = 7f)
{
    protected const float FlyThreshold = 250f;
    private const float PanicDirectionDuration = 3000f;
    private const float PanicTurnSpeed = 0.0095f;
    private const float PanicVelocityBlend = 0.2f;
    private const float PanicFuelBurnPerMs = 0.03f;
    private const float PanicLaunchSpeed = 13f;
    private const float PanicCruiseSpeedBonus = 8f;
    private const float PanicMinCruiseSpeed = 8f;
    private const float PanicMaxCruiseSpeed = 12f;
    private const float PanicWaveMinAmplitude = 0.2f;
    private const float PanicWaveMaxAmplitude = 0.65f;
    private const float PanicWaveMinFrequency = 0.0035f;
    private const float PanicWaveMaxFrequency = 0.0075f;
    private const float PanicWaveMinSecondaryScale = 0.15f;
    private const float PanicWaveMaxSecondaryScale = 0.35f;
    private const float PanicCrashArmTime = 2000f;
    private const float PanicCrashSpeed = 10.5f;
    private const float PanicCrashDistanceFactor = 0.75f;
    private const float PanicCrashMinDistance = 5f;
    private const float PanicCrashMaxDistance = 12f;
    private const float PanicCrashTunnelingDistance = 0f;
    private const float PanicCrashExplosionDamage = 120f;
    private const float PanicCrashSelfDamage = 150f;
    private static readonly Vector2[] PanicDirections =
    [
        new(0.95f, 0.35f),
        new(-0.95f, 0.35f),
        new(0.55f, 0.9f),
        new(-0.55f, 0.9f),
        new(0.2f, 1f),
        new(-0.2f, 1f),
        new(0.85f, -0.2f),
        new(-0.85f, -0.2f)
    ];

    protected internal readonly BarMeter Fuel = new(fuel, fuel);
    protected internal readonly float MaxSpeed = maxSpeed;

    protected float AirTime;
    protected float EffectTimer;

    protected Texture2D Jetpack;
    protected Texture2D JetpackBack;
    protected Texture2D JetpackDiving;
    internal bool Shake;
    protected float SoundTimer;

    private bool _panicFlight;
    private Vector2 _panicBaseDirection = new(0f, 1f);
    private Vector2 _panicDirection = new(0f, 1f);
    private float _panicDirectionTimer;
    private int _panicDirectionIndex = -1;
    private float _panicRotation;
    private int _panicRotationDirection = 1;
    private float _panicWaveAmplitude = PanicWaveMinAmplitude;
    private float _panicWaveFrequency = PanicWaveMinFrequency;
    private float _panicWavePhase;
    private float _panicWaveSecondaryPhase;
    private float _panicWaveSecondaryScale = PanicWaveMinSecondaryScale;
    private float _panicFlightTimer;
    private bool _panicCrashTriggered;

    protected internal JetpackState State;
    internal bool PanicFlightActive => _panicFlight;

    internal virtual void Update(float ms, ExtendedPlayer extendedPlayer)
    {
        Player player = extendedPlayer.Player;
        if (player.RocketRideProjectileWorldID != 0)
        {
            Discard(extendedPlayer);
            return;
        }

        if (_panicFlight)
        {
            UpdatePanicFlight(ms, extendedPlayer);
            return;
        }

        if (player.InAir && !(player.Diving || player.LedgeGrabbing || player.Climbing || player.Crouching || player.Staggering || player.LayingOnGround || player.Falling || player.IsCaughtByPlayer))
        {
            AirTime += ms;
        }
        else
        {
            AirTime = 0f;
            State = JetpackState.Idling;
        }

        if (AirTime > FlyThreshold && player.VirtualKeyboard.PressingKey(19))
        {
            if (State is JetpackState.Idling or JetpackState.Falling)
            {
                State = JetpackState.Flying;

                if (player.GameOwner != GameOwnerEnum.Client)
                {
                    SoundHandler.PlaySound("Bazooka", player.GameWorld);
                }
            }

            Vector2 velocity = player.CurrentVelocity;
            velocity.X *= player.SlowmotionFactor * 0.6f;

            velocity.Y = velocity.Y <= MaxSpeed
                ? (velocity.Y > 1.94f ? velocity.Y : MaxSpeed > 1 ? 1.94f : 1.94f * MaxSpeed) * player.SlowmotionFactor * 1.17f
                : MaxSpeed;

            // player.SetNewLinearVelocity(velocity);
            player.WorldBody.SetLinearVelocity(velocity);
            player.m_preBox2DLinearVelocity = velocity;
            player.AirControlBaseVelocity = velocity;
            player.ForceServerPositionState();
            player.ImportantUpdate = true;

            if (!player.InfiniteAmmo && player.GameOwner != GameOwnerEnum.Client)
            {
                Fuel.CurrentValue -= 0.03f * player.SlowmotionFactor * ms;

                if (player.GameOwner == GameOwnerEnum.Server)
                {
                    GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], player.ObjectID, extendedPlayer.GetStates()));
                }
            }

            if (player.GameOwner != GameOwnerEnum.Client)
            {
                EffectTimer -= ms;
                if (EffectTimer <= 0f)
                {
                    PlayEffect(player);
                }

                SoundTimer -= ms;
                if (SoundTimer < 0f)
                {
                    PlaySound(player);
                }
            }

            Shake = true;
        }
        else
        {
            if (State == JetpackState.Flying)
            {
                State = JetpackState.Falling;
            }

            Shake = false;
        }

        if (Fuel.CurrentValue <= 0 && extendedPlayer.JetpackType != JetpackType.None)
        {
            Discard(extendedPlayer);
        }
    }

    internal void StartPanicFlight(ExtendedPlayer extendedPlayer, Player hitBy)
    {
        StartPanicFlight(extendedPlayer, hitBy?.LastDirectionX ?? extendedPlayer.Player.LastDirectionX);
    }

    internal void StartPanicFlight(ExtendedPlayer extendedPlayer, int launchDirection)
    {
        Player player = extendedPlayer.Player;
        if (Fuel.CurrentValue <= 0f || (!_panicFlight && State != JetpackState.Flying) || player.IsRemoved || player.IsDead)
        {
            return;
        }

        if (launchDirection == 0)
        {
            launchDirection = 1;
        }

        _panicFlight = true;
        _panicFlightTimer = 0f;
        _panicRotation = player.Rotation;
        _panicRotationDirection = launchDirection > 0 ? 1 : -1;
        _panicCrashTriggered = false;
        PickPanicDirection(launchDirection);
        ApplyPanicVelocity(player, PanicLaunchSpeed);
        UpdatePanicRotation(player, _panicDirection, 0f, true);
        State = JetpackState.Flying;
        Shake = true;

        if (player.GameOwner == GameOwnerEnum.Server)
        {
            GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], player.ObjectID, extendedPlayer.GetStates()));
        }
    }

    internal void SetPanicFlightActive(Player player, bool active)
    {
        if (!active)
        {
            _panicFlight = false;
            return;
        }

        if (_panicFlight || Fuel.CurrentValue <= 0f || player.IsRemoved || player.IsDead)
        {
            return;
        }

        _panicFlight = true;
        _panicFlightTimer = 0f;
        _panicRotation = player.Rotation;
        _panicRotationDirection = player.LastDirectionX >= 0 ? 1 : -1;
        _panicCrashTriggered = false;
        PickPanicDirection();
        UpdatePanicRotation(player, _panicDirection, 0f, true);
        State = JetpackState.Flying;
        Shake = true;
    }

    private void UpdatePanicFlight(float ms, ExtendedPlayer extendedPlayer)
    {
        Player player = extendedPlayer.Player;
        if (player.WorldBody == null || player.IsRemoved || player.IsDead || player.IsCaughtByPlayer || player.IsGrabbedByPlayer)
        {
            _panicFlight = false;
            Shake = false;
            State = JetpackState.Falling;
            return;
        }

        _panicDirectionTimer -= ms;
        if (_panicDirectionTimer <= 0f)
        {
            PickPanicDirection();
        }
        UpdatePanicWaveDirection(ms, player.SlowmotionFactor);

        State = JetpackState.Flying;
        AirTime = Math.Max(AirTime, FlyThreshold + 1f);
        _panicFlightTimer += ms;

        float speed = MathHelper.Clamp(MaxSpeed * 1.4f + PanicCruiseSpeedBonus, PanicMinCruiseSpeed, PanicMaxCruiseSpeed);
        Vector2 targetVelocity = _panicDirection * speed * player.SlowmotionFactor;
        Vector2 velocity = player.WorldBody.GetLinearVelocity();
        velocity += (targetVelocity - velocity) * PanicVelocityBlend;

        player.WorldBody.SetLinearVelocity(velocity);
        player.m_preBox2DLinearVelocity = velocity;
        player.AirControlBaseVelocity = velocity;
        player.ForceServerPositionState();
        player.ImportantUpdate = true;

        UpdatePanicRotation(player, velocity.LengthSquared() > 0.01f ? velocity : _panicDirection, ms, false);

        if (TryExplodeOnPanicCrash(extendedPlayer, velocity))
        {
            return;
        }

        if (!player.InfiniteAmmo && player.GameOwner != GameOwnerEnum.Client)
        {
            Fuel.CurrentValue -= PanicFuelBurnPerMs * player.SlowmotionFactor * ms;

            if (player.GameOwner == GameOwnerEnum.Server)
            {
                GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], player.ObjectID, extendedPlayer.GetStates()));
            }
        }

        if (player.GameOwner != GameOwnerEnum.Client)
        {
            EffectTimer -= ms;
            if (EffectTimer <= 0f)
            {
                PlayEffect(player);
            }

            SoundTimer -= ms;
            if (SoundTimer < 0f)
            {
                PlaySound(player);
            }
        }

        Shake = true;

        if (Fuel.CurrentValue <= 0 && extendedPlayer.JetpackType != JetpackType.None)
        {
            Discard(extendedPlayer);
        }
    }

    private void PickPanicDirection(int forcedHorizontalDirection = 0)
    {
        int nextIndex = Globals.Random.Next(PanicDirections.Length);
        if (PanicDirections.Length > 1)
        {
            while (nextIndex == _panicDirectionIndex)
            {
                nextIndex = Globals.Random.Next(PanicDirections.Length);
            }
        }

        _panicDirectionIndex = nextIndex;
        _panicBaseDirection = PanicDirections[nextIndex];
        if (forcedHorizontalDirection != 0 && Math.Abs(_panicBaseDirection.X) > 0.05f)
        {
            _panicBaseDirection.X = Math.Abs(_panicBaseDirection.X) * (forcedHorizontalDirection > 0 ? 1f : -1f);
        }

        if (_panicBaseDirection == Vector2.Zero)
        {
            _panicBaseDirection = new Vector2(0f, 1f);
        }

        _panicBaseDirection.Normalize();
        RandomizePanicWave();
        UpdatePanicWaveDirection(0f, 1f);
        _panicDirectionTimer = PanicDirectionDuration;
    }

    private void RandomizePanicWave()
    {
        _panicWaveAmplitude = Globals.Random.NextFloat(PanicWaveMinAmplitude, PanicWaveMaxAmplitude);
        _panicWaveFrequency = Globals.Random.NextFloat(PanicWaveMinFrequency, PanicWaveMaxFrequency);
        _panicWavePhase = Globals.Random.NextFloat(0f, MathHelper.TwoPi);
        _panicWaveSecondaryPhase = Globals.Random.NextFloat(0f, MathHelper.TwoPi);
        _panicWaveSecondaryScale = Globals.Random.NextFloat(PanicWaveMinSecondaryScale, PanicWaveMaxSecondaryScale);
    }

    private void UpdatePanicWaveDirection(float ms, float slowmotionFactor)
    {
        _panicWavePhase += ms * _panicWaveFrequency * slowmotionFactor;
        Vector2 sideDirection = new(-_panicBaseDirection.Y, _panicBaseDirection.X);
        float wave = (float)Math.Sin(_panicWavePhase) * _panicWaveAmplitude;
        wave += (float)Math.Sin(_panicWaveSecondaryPhase + _panicWavePhase * 0.47f) * _panicWaveAmplitude * _panicWaveSecondaryScale;

        _panicDirection = _panicBaseDirection + sideDirection * wave;
        if (_panicDirection.LengthSquared() <= 0.0001f)
        {
            _panicDirection = _panicBaseDirection;
        }

        _panicDirection.Normalize();
    }

    private void UpdatePanicRotation(Player player, Vector2 direction, float ms, bool snap)
    {
        if (direction.LengthSquared() <= 0.0001f)
        {
            return;
        }

        direction.Normalize();
        float targetRotation = direction.GetRotatedAngle();
        float rotationDelta = MathHelper.WrapAngle(targetRotation - _panicRotation);
        if (snap || Math.Abs(rotationDelta) <= 0.001f)
        {
            _panicRotation = targetRotation;
        }
        else
        {
            float rotationStep = PanicTurnSpeed * ms * player.SlowmotionFactor;
            if (Math.Abs(rotationDelta) <= rotationStep)
            {
                _panicRotation = targetRotation;
            }
            else
            {
                _panicRotationDirection = rotationDelta > 0f ? 1 : -1;
                _panicRotation += rotationStep * _panicRotationDirection;
            }
        }

        if (Math.Abs(rotationDelta) > 0.001f)
        {
            _panicRotationDirection = rotationDelta > 0f ? 1 : -1;
        }

        player.Rotation = _panicRotation;
        player.LastFallingRotation = _panicRotation;
        player.RotationDirection = _panicRotationDirection;
    }

    private bool TryExplodeOnPanicCrash(ExtendedPlayer extendedPlayer, Vector2 velocity)
    {
        Player player = extendedPlayer.Player;
        if (_panicCrashTriggered || _panicFlightTimer < PanicCrashArmTime || player.GameOwner == GameOwnerEnum.Client || player.GameWorld == null)
        {
            return false;
        }

        float speed = velocity.Length();
        if (speed < PanicCrashSpeed)
        {
            return false;
        }

        Vector2 crashDirection = velocity;
        if (crashDirection.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        crashDirection.Normalize();
        float crashScanDistance = MathHelper.Clamp(speed * PanicCrashDistanceFactor, PanicCrashMinDistance, PanicCrashMaxDistance);
        GameWorld.RayCastResult hit = player.GameWorld.RayCast(player.Position, crashDirection, PanicCrashTunnelingDistance, crashScanDistance, IsPanicCrashFixture, _ => false);
        if (hit.EndFixture == null)
        {
            return false;
        }

        _panicCrashTriggered = true;
        _panicFlight = false;
        Shake = false;
        State = JetpackState.Falling;

        _ = player.GameWorld.TriggerExplosion(hit.EndPosition, PanicCrashExplosionDamage, true);
        if (!player.IsRemoved && !player.IsDead)
        {
            player.TakeMiscDamage(PanicCrashSelfDamage, sourceID: player.ObjectID);
        }

        Discard(extendedPlayer);
        return true;
    }

    private static bool IsPanicCrashFixture(Fixture fixture)
    {
        if (fixture == null || fixture.IsCloud() || fixture.GetBody().GetType() != BodyType.Static)
        {
            return false;
        }

        ObjectData objectData = ObjectData.Read(fixture);
        return objectData is { IsPlayer: false };
    }

    private void ApplyPanicVelocity(Player player, float speed)
    {
        if (player.WorldBody == null)
        {
            return;
        }

        Vector2 velocity = _panicDirection * speed * player.SlowmotionFactor;
        player.WorldBody.SetLinearVelocity(velocity);
        player.m_preBox2DLinearVelocity = velocity;
        player.AirControlBaseVelocity = velocity;
        player.ForceServerPositionState();
        player.ImportantUpdate = true;
    }

    protected abstract void PlayEffect(Player player);

    protected abstract void PlaySound(Player player);

    protected internal virtual void Discard(ExtendedPlayer extendedPlayer)
    {
        _panicFlight = false;
        _panicFlightTimer = 0f;
        _panicCrashTriggered = false;
        extendedPlayer.JetpackType = JetpackType.None;
        extendedPlayer.GenericJetpack = null;
        if (extendedPlayer.Player.GameOwner == GameOwnerEnum.Server)
        {
            GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], extendedPlayer.Player.ObjectID, extendedPlayer.GetStates()));
        }
    }

    internal abstract Texture2D GetJetpackTexture(string postFix);
}