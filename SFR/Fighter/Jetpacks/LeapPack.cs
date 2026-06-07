using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SFD;
using SFD.Effects;
using SFD.Sounds;
using SFD.Tiles;
using SFR.Sync.Generic;

namespace SFR.Fighter.Jetpacks;

internal sealed class LeapPack : GenericJetpack
{
    private const float LeapCooldown = 550f;
    private const float LeapFuelCost = 12f;
    private const float LeapHorizontalSpeed = 5.5f;
    private const float LeapVerticalSpeed = 10.5f;
    private const float LeapActiveTime = 220f;

    private float _cooldown;
    private float _leapActiveTimer;
    private bool _jumpWasPressed;
    private bool _wasInAir;

    internal LeapPack() : base(100, 3.5f)
    {
    }

    internal override void Update(float ms, ExtendedPlayer extendedPlayer)
    {
        if (PanicFlightActive)
        {
            base.Update(ms, extendedPlayer);
            return;
        }

        Player player = extendedPlayer.Player;
        if (player.RocketRideProjectileWorldID != 0)
        {
            Discard(extendedPlayer);
            return;
        }

        bool jumpPressed = player.VirtualKeyboard.PressingKey(19);
        _cooldown = Math.Max(0f, _cooldown - ms);

        if (_leapActiveTimer > 0f)
        {
            _leapActiveTimer -= ms;
            if (_leapActiveTimer <= 0f && State == JetpackState.Flying)
            {
                State = player.InAir ? JetpackState.Falling : JetpackState.Idling;
            }
        }

        if (jumpPressed && !_jumpWasPressed && _cooldown <= 0f && CanLeap(player))
        {
            PerformLeap(extendedPlayer);
        }
        else if (!player.InAir && _leapActiveTimer <= 0f)
        {
            State = JetpackState.Idling;
        }

        Shake = _leapActiveTimer > 0f;
        _jumpWasPressed = jumpPressed;
        _wasInAir = player.InAir;

        if (Fuel.CurrentValue <= 0f && extendedPlayer.JetpackType != JetpackType.None)
        {
            Discard(extendedPlayer);
        }
    }

    private bool CanLeap(Player player)
    {
        return Fuel.CurrentValue > 0f
            && player.InAir
            && _wasInAir
            && player.WorldBody != null
            && !player.IsRemoved
            && !player.IsDead
            && !(player.Diving || player.LedgeGrabbing || player.Climbing || player.Crouching || player.Staggering || player.LayingOnGround || player.IsCaughtByPlayer || player.IsGrabbedByPlayer || player.Rolling);
    }

    private void PerformLeap(ExtendedPlayer extendedPlayer)
    {
        Player player = extendedPlayer.Player;
        int horizontalDirection = GetHorizontalDirection(player);
        float slowmotionFactor = Math.Max(player.SlowmotionFactor, 0.1f);
        Vector2 velocity = player.WorldBody.GetLinearVelocity();

        velocity.X = horizontalDirection * Math.Max(Math.Abs(velocity.X), LeapHorizontalSpeed * slowmotionFactor);
        velocity.Y = Math.Max(velocity.Y, LeapVerticalSpeed * slowmotionFactor);

        player.WorldBody.SetLinearVelocity(velocity);
        player.m_preBox2DLinearVelocity = velocity;
        player.AirControlBaseVelocity = velocity;
        player.ForceServerPositionState();
        player.ImportantUpdate = true;

        State = JetpackState.Flying;
        AirTime = FlyThreshold + 1f;
        _cooldown = LeapCooldown;
        _leapActiveTimer = LeapActiveTime;
        Shake = true;

        if (!player.InfiniteAmmo && player.GameOwner != GameOwnerEnum.Client)
        {
            Fuel.CurrentValue -= LeapFuelCost;
            SyncFuel(extendedPlayer);
        }

        if (player.GameOwner != GameOwnerEnum.Client)
        {
            PlayEffect(player);
            PlaySound(player);
        }
    }

    private static int GetHorizontalDirection(Player player)
    {
        if (player.VirtualKeyboard.PressingKey(2))
        {
            return -1;
        }

        if (player.VirtualKeyboard.PressingKey(3))
        {
            return 1;
        }

        Vector2 velocity = player.WorldBody.GetLinearVelocity();
        return Math.Abs(velocity.X) > 1f ? Math.Sign(velocity.X) : player.LastDirectionX;
    }

    private static void SyncFuel(ExtendedPlayer extendedPlayer)
    {
        Player player = extendedPlayer.Player;
        if (player.GameOwner == GameOwnerEnum.Server)
        {
            GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], player.ObjectID, extendedPlayer.GetStates()));
        }
    }

    protected override void PlayEffect(Player player)
    {
        EffectHandler.PlayEffect("FNDTRA", player.Position + new Vector2(-4 * player.LastDirectionX, -6), player.GameWorld);
        EffectTimer = 35f;
    }

    protected override void PlaySound(Player player)
    {
        SoundHandler.PlaySound("Bazooka", player.GameWorld);
        SoundTimer = 90f;
    }

    internal override Texture2D GetJetpackTexture(string postFix)
    {
        Jetpack ??= Textures.GetTexture("LeapPack");
        JetpackBack ??= Textures.GetTexture("LeapPackBack");
        JetpackDiving ??= Textures.GetTexture("LeapPackDiving");

        Texture2D texture = postFix switch
        {
            "" => Jetpack,
            "Back" => JetpackBack,
            "Diving" => JetpackDiving,
            _ => null
        };

        return texture;
    }

    protected internal override void Discard(ExtendedPlayer extendedPlayer)
    {
        base.Discard(extendedPlayer);
        Player player = extendedPlayer.Player;

        _ = player.GameWorld.CreateTile("JetpackDebris", player.Position, 0);
    }
}