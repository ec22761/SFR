using System;
using System.Runtime.CompilerServices;
using SFD;
using SFD.Sounds;
using SFDGameScriptInterface;
using SFR.Fighter.Jetpacks;
using SFR.Sync.Generic;

namespace SFR.Fighter;

/// <summary>
/// Since we need to save additional data into the player instance
/// we use this file to "extend" the player class.
/// </summary>
internal sealed class ExtendedPlayer : IEquatable<Player>, IEquatable<ExtendedPlayer>
{
    internal static readonly ConditionalWeakTable<Player, ExtendedPlayer> ExtendedPlayersTable = new();
    internal readonly Player Player;
    internal readonly TimeSequence Time = new();
    internal GenericJetpack GenericJetpack;
    internal JetpackType JetpackType = JetpackType.None;

    internal ExtendedPlayer(Player player) => Player = player;

    internal bool AdrenalineBoost
    {
        get => Time.AdrenalineBoost > 0f;
        set => Time.AdrenalineBoost = value ? TimeSequence.AdrenalineBoostTime : 0f;
    }

    internal bool LeapBoost
    {
        get => Time.LeapBoost > 0f;
        set => Time.LeapBoost = value ? TimeSequence.LeapBoostTime : 0f;
    }

    internal bool Electrocuted
    {
        get => Time.Electrocution > 0f;
        set => Time.Electrocution = value ? TimeSequence.ElectrocutionTime : 0f;
    }

    internal bool Poisoned
    {
        get => Time.Poison > 0f;
        set => Time.Poison = value ? TimeSequence.PoisonTime : 0f;
    }

    internal bool Spectral
    {
        get => Time.Spectral > 0f;
        set => Time.Spectral = value ? TimeSequence.SpectralTime : 0f;
    }

    /// <summary>
    ///     Tracks whether the player was on the ground last frame,
    ///     so we can detect jump transitions for the leap boost.
    /// </summary>
    internal bool WasGrounded;

    public bool Equals(Player other) => other?.ObjectID == Player.ObjectID;

    internal void ApplyAdrenalineBoost()
    {
        AdrenalineBoost = true;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
    }

    internal void ApplyLeapBoost()
    {
        LeapBoost = true;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
    }

    internal object[] GetStates()
    {
        object[] states = [AdrenalineBoost, (int)JetpackType, GenericJetpack?.Fuel?.CurrentValue ?? 0f, LeapBoost, Electrocuted, Poisoned, Spectral];
        return states;
    }

    internal void DisableAdrenalineBoost()
    {
        AdrenalineBoost = false;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
        SoundHandler.PlaySound("StrengthBoostStop", Player.Position, Player.GameWorld);
    }

    internal void DisableLeapBoost()
    {
        LeapBoost = false;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
        SoundHandler.PlaySound("StrengthBoostStop", Player.Position, Player.GameWorld);
    }

    internal void DisableElectrocution()
    {
        Electrocuted = false;
        if (!Player.IsDead && !Player.IsRemoved)
        {
            Player.SetInputMode(PlayerInputMode.Enabled);
            Player.DeathKneeling = false;
        }

        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
    }

    internal void ApplyPoison()
    {
        Poisoned = true;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
    }

    internal void DisablePoison()
    {
        Poisoned = false;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
    }

    internal void ApplySpectral()
    {
        Spectral = true;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
    }

    internal void DisableSpectral()
    {
        Spectral = false;
        GenericData.SendGenericDataToClients(new GenericData(DataType.ExtraClientStates, [], Player.ObjectID, GetStates()));
        SoundHandler.PlaySound("StrengthBoostStop", Player.Position, Player.GameWorld);
    }

    internal class TimeSequence
    {
        internal const float AdrenalineBoostTime = 20000f;
        internal const float LeapBoostTime = 20000f;
        internal const float ElectrocutionTime = 6000f;
        internal const float PoisonTime = 5000f;
        internal const float PoisonDamagePerTick = 2f;
        internal const float PoisonTickInterval = 500f;
        internal const float SpectralTime = 30000f;
        internal float AdrenalineBoost;
        internal float LeapBoost;
        internal float Electrocution;
        internal float Poison;
        internal float PoisonTickTimer;
        internal float Spectral;
    }

    public bool Equals(ExtendedPlayer other) => other?.Player.ObjectID == Player.ObjectID;
}