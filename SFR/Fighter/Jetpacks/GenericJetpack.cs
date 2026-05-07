using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Sounds;
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
    private const float PanicSpinSpeed = 0.0095f;
    private const float PanicVelocityBlend = 0.2f;
    private const float PanicFuelBurnPerMs = 0.03f;
    private const float PanicLaunchSpeed = 13f;
    private const float PanicCruiseSpeedBonus = 8f;
    private const float PanicMinCruiseSpeed = 8f;
    private const float PanicMaxCruiseSpeed = 12f;
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
    private Vector2 _panicDirection = new(0f, 1f);
    private float _panicDirectionTimer;
    private int _panicDirectionIndex = -1;
    private float _panicSpin;
    private int _panicSpinDirection = 1;

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
        Player player = extendedPlayer.Player;
        if (Fuel.CurrentValue <= 0f || (!_panicFlight && State != JetpackState.Flying) || player.IsRemoved || player.IsDead)
        {
            return;
        }

        int launchDirection = hitBy == null ? player.LastDirectionX : hitBy.LastDirectionX;
        if (launchDirection == 0)
        {
            launchDirection = 1;
        }

        _panicFlight = true;
        _panicSpin = player.Rotation;
        _panicSpinDirection = launchDirection > 0 ? 1 : -1;
        PickPanicDirection(launchDirection);
        ApplyPanicVelocity(player, PanicLaunchSpeed);
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
        _panicSpin = player.Rotation;
        _panicSpinDirection = player.LastDirectionX >= 0 ? 1 : -1;
        PickPanicDirection();
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

        State = JetpackState.Flying;
        AirTime = Math.Max(AirTime, FlyThreshold + 1f);

        float speed = MathHelper.Clamp(MaxSpeed * 1.4f + PanicCruiseSpeedBonus, PanicMinCruiseSpeed, PanicMaxCruiseSpeed);
        Vector2 targetVelocity = _panicDirection * speed * player.SlowmotionFactor;
        Vector2 velocity = player.WorldBody.GetLinearVelocity();
        velocity += (targetVelocity - velocity) * PanicVelocityBlend;

        player.WorldBody.SetLinearVelocity(velocity);
        player.m_preBox2DLinearVelocity = velocity;
        player.AirControlBaseVelocity = velocity;
        player.ForceServerPositionState();
        player.ImportantUpdate = true;

        _panicSpin += PanicSpinSpeed * ms * _panicSpinDirection * player.SlowmotionFactor;
        player.Rotation = _panicSpin;
        player.LastFallingRotation = _panicSpin;
        player.RotationDirection = _panicSpinDirection;

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
        _panicDirection = PanicDirections[nextIndex];
        if (forcedHorizontalDirection != 0 && Math.Abs(_panicDirection.X) > 0.05f)
        {
            _panicDirection.X = Math.Abs(_panicDirection.X) * (forcedHorizontalDirection > 0 ? 1f : -1f);
        }

        if (_panicDirection == Vector2.Zero)
        {
            _panicDirection = new Vector2(0f, 1f);
        }

        _panicDirection.Normalize();
        _panicDirectionTimer = PanicDirectionDuration;
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
        extendedPlayer.JetpackType = JetpackType.None;
        extendedPlayer.GenericJetpack = null;
        if (extendedPlayer.Player.GameOwner == GameOwnerEnum.Server)
        {
            GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], extendedPlayer.Player.ObjectID, extendedPlayer.GetStates()));
        }
    }

    internal abstract Texture2D GetJetpackTexture(string postFix);
}