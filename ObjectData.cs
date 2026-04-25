using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Networking.LidgrenAdapter;
using SFD.Core;
using SFD.Effects;
using SFD.Materials;
using SFD.Objects;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.Tiles;
using SFDGameScriptInterface;

namespace SFD;

public abstract class ObjectData : NetMessageCount, IObjectProperties
{
	public struct FireValues
	{
		public const float SMOKE_TRESHOLD_IGNITION_VALUE = 5f;

		public const float DEFAULT_SMOKE_TIME = 3000f;

		public const float DEFAULT_BURN_TIME = 5000f;

		public float IgnitionValue;

		public float SmokeTime;

		public float BurnTime;

		public bool IsSmoking
		{
			get
			{
				if (!(SmokeTime > 0f))
				{
					return IgnitionValue >= 5f;
				}
				return true;
			}
		}

		public bool IsBurning => BurnTime > 0f;
	}

	public enum DamageType
	{
		Impact,
		PlayerImpact,
		Player,
		Projectile,
		Explosion,
		Fire,
		OtherScripts
	}

	public enum ObjectDataValue
	{
		MapObjectID = 0,
		CustomID = 1,
		WorldPosition = 2,
		Angle = 3,
		LocalRenderLayer = 4,
		FaceDirection = 5,
		Colors = 6,
		Properties = 7,
		LinearVelocity = 8,
		AngularVelocity = 9,
		GroupID = 10,
		END = 9999
	}

	public FireValues Fire;

	public float LastClientHealthCheck;

	public short m_faceDirection;

	public SpriteEffects m_faceDirectionSpriteEffect;

	public ushort m_groupID;

	public float m_lastImpactTimestamp;

	public float[] m_totalGibbFramePressures = new float[5];

	public int m_totalGibbFramePressuresIndex;

	public int m_totalGibbFramePressuresOverZero;

	public int m_gibbingLastFrameCounter;

	public float m_minimumGibbPressureLast12Frames;

	public float m_minimumGibbPressureLast12FramesCounter;

	public bool m_isTranslucencePointer;

	public bool m_isAITargetableObject;

	public ObjectAITargetData m_AITargetData = ObjectAITargetData.Default;

	public BarMeter m_health;

	public List<Fixture> m_owners;

	public Body m_body;

	public List<ObjectDecal> m_objectDecals;

	public string[] m_colors;

	public bool m_hasColors;

	public bool UpdateColorsQueued;

	public bool m_updateEnabled;

	public int m_fixtureSizeXMultiplier = 1;

	public int m_fixtureSizeYMultiplier = 1;

	public int m_lastSizeableWidth;

	public int m_lastSizeableHeight;

	public Microsoft.Xna.Framework.Vector2 m_solidColorOrigin = Microsoft.Xna.Framework.Vector2.One;

	public Microsoft.Xna.Framework.Vector2 m_solidColorScale = Microsoft.Xna.Framework.Vector2.Zero;

	public float ImpactTresholdValue = 2f;

	public float ImpactDamageTresholdValue = 0.1f;

	public bool m_isDisposed;

	public Dictionary<ObjectDataSyncedMethod.Methods, uint> m_syncedMethodCounter;

	public object InternalData { get; set; }

	public object CustomData { get; set; }

	public int IgnoreKickVelocityForPlayerID { get; set; }

	public float IgnoreKickVelocityForPlayerElapsedTotalTime { get; set; }

	public bool HasGlobalFootstepSound { get; set; }

	public ProjectileTunnelingCheck ProjectileTunnelingCheck { get; set; }

	public bool AllowDynamicPathNodeConnections { get; set; }

	public bool InstaGibPlayer { get; set; }

	public bool BotAIIgnoreObjectAsCover { get; set; }

	public bool BotAIForceRegisterCoverCollision { get; set; }

	public bool BreakOnDive { get; set; }

	public bool BreakOnStagger { get; set; }

	public Tile.TYPE Type { get; set; }

	public int LocalRenderLayer { get; set; }

	public int LocalDrawCategory { get; set; }

	public bool IsFarBG { get; set; }

	public bool ClientSyncEnableContactsOnDestroyedByPlayerImpact { get; set; }

	public bool ClientSyncDisableAnglePositionClippingCheck { get; set; }

	public short FaceDirection
	{
		get
		{
			return m_faceDirection;
		}
		set
		{
			if (m_faceDirection != value)
			{
				m_faceDirection = value;
				m_faceDirectionSpriteEffect = ((m_faceDirection != 1) ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
				if (GameOwner == GameOwnerEnum.Server && GameWorld != null && GameWorld.FirstUpdateIsRun)
				{
					SyncTransform();
				}
			}
		}
	}

	public bool IsGroupMarker => MapObjectID == "GROUPMARKER";

	public string MapObjectID { get; set; }

	public int ObjectID { get; set; }

	public int CustomID { get; set; }

	public int PreservedJointsFromObjectID { get; set; }

	public string CustomIDName
	{
		get
		{
			if (Properties.Exists(ObjectPropertyID.Object_CustomID))
			{
				return (string)Properties.Get(ObjectPropertyID.Object_CustomID).Value;
			}
			return "";
		}
		set
		{
			if (Properties.Exists(ObjectPropertyID.Object_CustomID))
			{
				if (!GameWorld.CustomIDTableLookup.ContainsKey(value))
				{
					GameWorld.CustomIDTableLookup.Add(value, GameWorld.CustomIDTableLookup.Count + 1);
				}
				CustomID = GameWorld.CustomIDTableLookup[value];
				Properties.Get(ObjectPropertyID.Object_CustomID).Value = value;
			}
		}
	}

	public ushort GroupID => m_groupID;

	public int BodyID { get; set; }

	public BodyData BodyData { get; set; }

	public bool RemovalInitiated { get; set; }

	public bool DestructionInitiated { get; set; }

	public bool TerminationInitiated => RemovalInitiated | DestructionInitiated;

	public bool IsPlayer => Type == Tile.TYPE.Player;

	public float ActivateRange { get; set; }

	public bool IsDynamic => Body.GetType() == Box2D.XNA.BodyType.Dynamic;

	public bool IsStatic => Body.GetType() == Box2D.XNA.BodyType.Static;

	public bool IgnoreBounceChecks { get; set; }

	public float MinimalGibbPressure { get; set; }

	public float MinimalGibbPressureLast12Frames { get; set; }

	public ObjectMissileData MissileData { get; set; }

	public bool IsAITargetableObject
	{
		get
		{
			return m_isAITargetableObject;
		}
		set
		{
			if (m_isAITargetableObject == value)
			{
				return;
			}
			m_isAITargetableObject = value;
			if (GameWorld != null)
			{
				if (value)
				{
					GameWorld.m_AITargetableObjects.Add(this);
				}
				else
				{
					GameWorld.m_AITargetableObjects.Remove(this);
				}
			}
		}
	}

	public ObjectAITargetData AITargetData
	{
		get
		{
			return m_AITargetData;
		}
		set
		{
			m_AITargetData = value;
		}
	}

	public virtual bool Activateable { get; set; }

	public virtual bool ActivateableHighlightning { get; set; }

	public bool StickyFeet { get; set; }

	public bool EditorOnly { get; set; }

	public BarMeter Health
	{
		get
		{
			return m_health;
		}
		set
		{
			m_health = value;
		}
	}

	public bool Destructable { get; set; }

	public List<Fixture> Owners => m_owners;

	public Body Body => m_body;

	public IObject ScriptBridge { get; set; }

	public ObjectProperties Properties { get; set; }

	public float CreateTime { get; set; }

	public float TotalExistTime => GameWorld.ElapsedTotalGameTime - CreateTime;

	public Tile Tile { get; set; }

	public Texture2D Texture { get; set; }

	public TileAnimation CurrentAnimation { get; set; }

	public string[] Colors => m_colors;

	public string[] ColorsCopy
	{
		get
		{
			if (m_colors == null)
			{
				return null;
			}
			string[] array = new string[m_colors.Length];
			for (int i = 0; i < m_colors.Length; i++)
			{
				array[i] = m_colors[i];
			}
			return array;
		}
	}

	public bool HasColors => m_hasColors;

	public bool UpdateEnabled => m_updateEnabled;

	public GameWorld GameWorld { get; set; }

	public GameOwnerEnum GameOwner { get; set; }

	public int FixtureSizeXMultiplier => m_fixtureSizeXMultiplier;

	public int FixtureSizeYMultiplier => m_fixtureSizeYMultiplier;

	public bool TextureSolidColor { get; set; }

	public Microsoft.Xna.Framework.Vector2 LocalBox2DBodyCenterPoint { get; set; }

	public bool RunOnDamageOtherScriptTypeActive { get; set; }

	public bool DoTakeImpactDamage { get; set; }

	public bool DoTakePlayerImpactDamage { get; set; }

	public bool DoMergeProjectileHitsForStaticTiles { get; set; }

	public bool DoTakeProjectileDamage { get; set; }

	public bool DoTakeExplosionDamage { get; set; }

	public virtual bool CanTakeFireDamage
	{
		get
		{
			if (DoTakeFireDamage && Destructable)
			{
				return Tile.Material.Resistance.Fire.Modifier > 0f;
			}
			return false;
		}
	}

	public virtual bool CanBurn
	{
		get
		{
			if (Tile.Material.Resistance.Fire.Modifier > 0f)
			{
				return Tile.Material.Flammable;
			}
			return false;
		}
	}

	public bool TrackingFireValues { get; set; }

	public bool DoTakeFireDamage { get; set; }

	public bool DrawFlag { get; set; }

	public bool DoDraw { get; set; }

	public bool DoDrawUpdate { get; set; }

	public bool DrawOutsideCameraArea { get; set; }

	public bool DoImpactHit { get; set; }

	public bool DoExplosionHit { get; set; }

	public bool DoDestroyObject { get; set; }

	public bool IsDisposed => m_isDisposed;

	public virtual void SetMaxFire()
	{
		if (CanBurn)
		{
			Fire.SmokeTime = 3000f;
			Fire.BurnTime = 5000f;
			Fire.IgnitionValue = Tile.Material.Resistance.Fire.Threshold;
			StartTrackingFireValues();
		}
		else if (CanTakeFireDamage)
		{
			Fire.SmokeTime = 3000f;
			Fire.IgnitionValue = Math.Min(Tile.Material.Resistance.Fire.Threshold, 5f);
			StartTrackingFireValues();
		}
	}

	public virtual void ClearFire()
	{
		Fire.SmokeTime = 0f;
		Fire.BurnTime = 0f;
		Fire.IgnitionValue = 0f;
	}

	public void SetGroupID(ushort groupID)
	{
		GameWorld.RemoveFromGroup(this, GroupID);
		m_groupID = groupID;
		if (groupID != 0)
		{
			GameWorld.AddToGroup(this, GroupID);
		}
	}

	public void PlayImpactEffect(ImpactHitEventArgs args)
	{
		if (GameWorld.ElapsedTotalGameTime - m_lastImpactTimestamp > 150f)
		{
			m_lastImpactTimestamp = GameWorld.ElapsedTotalGameTime;
			if (!string.IsNullOrWhiteSpace(Tile.ImpactEffect))
			{
				EffectHandler.PlayEffect(Tile.ImpactEffect, GetWorldPosition(), GameWorld);
			}
			if (!string.IsNullOrWhiteSpace(Tile.ImpactSound))
			{
				SoundHandler.PlaySound(Tile.ImpactSound, GetWorldPosition(), GameWorld);
			}
		}
	}

	public void ClearGibbFramePressure()
	{
		m_totalGibbFramePressures[0] = 0f;
		m_totalGibbFramePressures[1] = 0f;
		m_totalGibbFramePressures[2] = 0f;
		m_totalGibbFramePressures[3] = 0f;
		m_totalGibbFramePressures[4] = 0f;
		m_totalGibbFramePressuresOverZero = 0;
		m_totalGibbFramePressuresIndex = 0;
		m_minimumGibbPressureLast12Frames = 0f;
		m_minimumGibbPressureLast12FramesCounter = 0f;
		MinimalGibbPressureLast12Frames = 0f;
	}

	public void AddGibbFramePressure(float frameGibbPressure, int currentGibFrameCounter)
	{
		int num = currentGibFrameCounter - m_gibbingLastFrameCounter - 1;
		if (num > 0)
		{
			m_minimumGibbPressureLast12Frames = 0f;
			m_minimumGibbPressureLast12FramesCounter = 0f;
			MinimalGibbPressureLast12Frames = 0f;
			if (num >= m_totalGibbFramePressures.Length)
			{
				ClearGibbFramePressure();
			}
			else
			{
				for (int i = 0; i < num; i++)
				{
					m_totalGibbFramePressuresIndex++;
					if (m_totalGibbFramePressuresIndex >= m_totalGibbFramePressures.Length)
					{
						m_totalGibbFramePressuresIndex = 0;
					}
					if (m_totalGibbFramePressures[m_totalGibbFramePressuresIndex] > 0f)
					{
						m_totalGibbFramePressuresOverZero--;
					}
					m_totalGibbFramePressures[m_totalGibbFramePressuresIndex] = 0f;
				}
			}
		}
		int totalGibbFramePressuresIndex = m_totalGibbFramePressuresIndex;
		m_totalGibbFramePressuresIndex++;
		if (m_totalGibbFramePressuresIndex >= m_totalGibbFramePressures.Length)
		{
			m_totalGibbFramePressuresIndex = 0;
		}
		if (m_totalGibbFramePressures[totalGibbFramePressuresIndex] > 0f && m_totalGibbFramePressures[totalGibbFramePressuresIndex] - frameGibbPressure > Converter.Box2DMassFor10KG)
		{
			m_totalGibbFramePressures[totalGibbFramePressuresIndex] = 0f;
			m_totalGibbFramePressuresOverZero--;
		}
		if (frameGibbPressure > 0f)
		{
			if (m_totalGibbFramePressures[m_totalGibbFramePressuresIndex] <= 0f)
			{
				m_totalGibbFramePressuresOverZero++;
			}
		}
		else if (m_totalGibbFramePressures[m_totalGibbFramePressuresIndex] > 0f)
		{
			m_totalGibbFramePressuresOverZero--;
		}
		m_totalGibbFramePressures[m_totalGibbFramePressuresIndex] = frameGibbPressure;
		MinimalGibbPressure = 0f;
		if (m_totalGibbFramePressuresOverZero >= 3)
		{
			for (int j = 0; j < m_totalGibbFramePressures.Length; j++)
			{
				if (m_totalGibbFramePressures[j] != 0f)
				{
					if (MinimalGibbPressure == 0f)
					{
						MinimalGibbPressure = m_totalGibbFramePressures[j];
					}
					else if (m_totalGibbFramePressures[j] > 0f)
					{
						MinimalGibbPressure = Math.Min(m_totalGibbFramePressures[j], MinimalGibbPressure);
					}
				}
			}
		}
		if (frameGibbPressure == 0f)
		{
			m_minimumGibbPressureLast12Frames = 0f;
			m_minimumGibbPressureLast12FramesCounter = 0f;
			MinimalGibbPressureLast12Frames = 0f;
		}
		else
		{
			if (m_minimumGibbPressureLast12Frames == 0f || frameGibbPressure < m_minimumGibbPressureLast12Frames)
			{
				m_minimumGibbPressureLast12Frames = frameGibbPressure;
			}
			m_minimumGibbPressureLast12FramesCounter += 1f;
			if (m_minimumGibbPressureLast12FramesCounter == 12f)
			{
				MinimalGibbPressureLast12Frames = m_minimumGibbPressureLast12Frames;
			}
			else if (m_minimumGibbPressureLast12FramesCounter > 12f)
			{
				m_minimumGibbPressureLast12Frames = 0f;
				m_minimumGibbPressureLast12FramesCounter = 0f;
			}
		}
		m_gibbingLastFrameCounter = currentGibFrameCounter;
	}

	public virtual bool ActivateableForPlayer(Player player)
	{
		return Activateable;
	}

	public virtual bool ActivateableHighlightningForPlayer(Player player)
	{
		return ActivateableHighlightning;
	}

	public void SetStickyFeet(bool value)
	{
		Properties.Get(ObjectPropertyID.Object_StickyFeet).Value = value;
	}

	public void UpdateStickyFeet_Internally(bool value)
	{
		StickyFeet = value;
	}

	public float GetBodyMass()
	{
		if (Body == null)
		{
			return 0f;
		}
		return Body.GetMass();
	}

	public void SetBodyMass(float mass)
	{
		Properties.Get(ObjectPropertyID.Object_BodyMass).Value = mass;
	}

	public void UpdateBodyMass_Internally(float mass)
	{
		if (Body != null && mass > 0f)
		{
			Body.GetMassData(out var massData);
			float num = ((massData.mass != 0f) ? (mass / massData.mass) : 1f);
			massData.mass = mass;
			massData.I *= num;
			Body.SetMassData(ref massData);
		}
	}

	public virtual BaseObject GetScriptBridge()
	{
		return ScriptBridge;
	}

	public Fixture GetFixtureById(string id)
	{
		Fixture fixture = Body.GetFixtureList();
		while (true)
		{
			if (fixture != null)
			{
				if (fixture.ID == id)
				{
					break;
				}
				fixture = fixture.GetNext();
				continue;
			}
			return null;
		}
		return fixture;
	}

	public Fixture GetFixtureByIndex(int index)
	{
		Fixture fixture = Body.GetFixtureList();
		while (true)
		{
			if (fixture != null)
			{
				if (fixture.TileFixtureIndex == index)
				{
					break;
				}
				fixture = fixture.GetNext();
				continue;
			}
			return null;
		}
		return fixture;
	}

	public int GetFixtureIndex(Fixture fixture)
	{
		Fixture fixture2 = Body.GetFixtureList();
		while (true)
		{
			if (fixture2 != null)
			{
				if (fixture2 == fixture)
				{
					break;
				}
				fixture2 = fixture2.GetNext();
				continue;
			}
			return -1;
		}
		return fixture2.TileFixtureIndex;
	}

	public bool IsPunchable(Player sourcePlayer = null)
	{
		if (IsPlayer)
		{
			return true;
		}
		Body.GetFixtureList().GetFilterData(out var filter);
		return filter.punchable & (sourcePlayer == null || Body != sourcePlayer.StandingOnBody);
	}

	public ObjectProperties GetProperties()
	{
		return Properties;
	}

	public void AddOwner(Fixture ownerToAdd)
	{
		if (ownerToAdd == null)
		{
			throw new ArgumentException("Param musn't be null", "ownerToAdd");
		}
		if (m_body != null)
		{
			if (ownerToAdd.GetBody() != m_body)
			{
				throw new Exception("Error: All owners must share the same body");
			}
			foreach (Fixture owner in m_owners)
			{
				if (owner == ownerToAdd)
				{
					throw new Exception("Error: Can't add the same owner twice ObjectData.AddOwner()");
				}
			}
		}
		else
		{
			m_body = ownerToAdd.GetBody();
			BodyData bodyData = BodyData.Read(m_body);
			bodyData.AddObjectData(this);
			BodyID = bodyData.BodyID;
			BodyData = bodyData;
		}
		m_owners.Add(ownerToAdd);
	}

	public void RemoveOwner(Fixture ownerToRemove)
	{
		m_owners.Remove(ownerToRemove);
		if (m_owners.Count == 0)
		{
			BodyData.Read(m_body).RemoveObjectData(this);
			m_body = null;
		}
	}

	public void SetRenderLayer(int layer)
	{
		RemoveFromRenderLayer();
		AddToRenderLayer(layer);
	}

	public void BringToFront()
	{
		SetZOrder(GameWorld.RenderCategories[Tile.DrawCategory].GetLayer(LocalRenderLayer).Items.Count - 1);
	}

	public void SetZOrder(int zOrder)
	{
		List<ObjectData> items = GameWorld.RenderCategories[Tile.DrawCategory].GetLayer(LocalRenderLayer).Items;
		items.Remove(this);
		if (zOrder < items.Count && items[zOrder] == null)
		{
			items[zOrder] = this;
			return;
		}
		while (zOrder >= items.Count + 1)
		{
			items.Add(null);
		}
		items.Insert(zOrder, this);
	}

	public int GetZOrder()
	{
		return GameWorld.RenderCategories[Tile.DrawCategory].GetLayer(LocalRenderLayer).Items.IndexOf(this);
	}

	public void UnsyncSyncedAnimation()
	{
		if (CurrentAnimation != null && CurrentAnimation.IsSynced)
		{
			CurrentAnimation = CurrentAnimation.Copy();
			CurrentAnimation.IsSynced = false;
			DoDrawUpdate = true;
		}
	}

	public void SetAnimationDataIfAvailable()
	{
		if (Tile == null || GameWorld == null)
		{
			return;
		}
		TileAnimation tileAnimation = Tile.GetTileAnimation(Tile.TextureName);
		if (tileAnimation != null)
		{
			if (!tileAnimation.IsSynced)
			{
				CurrentAnimation = tileAnimation.Copy();
				DoDrawUpdate = true;
			}
			else
			{
				string key = $"{Tile.Name}_{Tile.TextureName}_{tileAnimation.Key}";
				if (!GameWorld.SyncedTileAnimations.ContainsKey(key))
				{
					GameWorld.SyncedTileAnimations.Add(key, tileAnimation.Copy());
				}
				CurrentAnimation = GameWorld.SyncedTileAnimations[key];
			}
			CurrentAnimation.CalculateTexture(Texture);
		}
		else
		{
			CurrentAnimation = null;
		}
	}

	public void SetTile(Tile tile)
	{
		Tile = tile;
		IsFarBG = tile.DrawCategory == 0 || tile.DrawCategory == 26;
		ActivateRange = tile.ActivateRange;
		if (tile.Life > 0f)
		{
			Health = new BarMeter(tile.Life, tile.Life);
			Destructable = !(tile.Life <= 0f);
		}
		else
		{
			Destructable = false;
		}
		Type = tile.Type;
		AllowDynamicPathNodeConnections = tile.AllowDynamicPathNodeConnections;
		BreakOnDive = tile.BreakOnDive;
		BreakOnStagger = tile.BreakOnStagger;
		ClientSyncDisableAnglePositionClippingCheck = tile.ClientSyncDisableAnglePositionClippingCheck;
		ClientSyncEnableContactsOnDestroyedByPlayerImpact = tile.ClientSyncEnableContactsOnDestroyedByPlayerImpact;
		InstaGibPlayer = tile.InstaGibPlayer;
		DoTakeExplosionDamage = tile.DefaultDoTakeExplosionDamage;
		DoTakeFireDamage = tile.DefaultDoTakeFireDamage;
		DoTakeImpactDamage = tile.DefaultDoTakeImpactDamage;
		DoTakePlayerImpactDamage = tile.DefaultDoTakePlayerImpactDamage;
		DoTakeProjectileDamage = tile.DefaultDoTakeProjectileDamage;
		if (tile.ColorPalette != null)
		{
			for (int i = 0; i < 3; i++)
			{
				ApplyColor(i, tile.ColorPalette.GetFirstColorFromLevel(i));
			}
		}
	}

	public bool ApplyColor(int level, string colorName, bool syncUpdate = false)
	{
		if (level >= 0 && level < 3 && !string.IsNullOrWhiteSpace(colorName))
		{
			if (m_colors == null)
			{
				m_colors = new string[3];
				for (int i = 0; i < m_colors.Length; i++)
				{
					m_colors[i] = "";
				}
				m_hasColors = false;
			}
			if (Tile.ColorPalette != null && Tile.ColorPalette.ContainsColorPackageForLevel(level, colorName))
			{
				if (m_colors[level] != colorName)
				{
					m_colors[level] = colorName;
					ColorsUpdated();
					if (syncUpdate && GameOwner == GameOwnerEnum.Server)
					{
						SyncColors();
					}
				}
				return true;
			}
			return false;
		}
		return false;
	}

	public bool ApplyColors(string[] colorNames, bool syncUpdate = false)
	{
		bool flag = false;
		if (colorNames == null)
		{
			m_colors = null;
			m_hasColors = false;
		}
		if (m_colors == null)
		{
			m_colors = new string[3];
			for (int i = 0; i < 3; i++)
			{
				m_colors[i] = "";
			}
			flag = true;
		}
		for (int j = 0; j < 3; j++)
		{
			if (Tile.ColorPalette != null)
			{
				if (colorNames != null && Tile.ColorPalette.ContainsColorPackageForLevel(j, colorNames[j]))
				{
					if (m_colors[j] != colorNames[j])
					{
						m_colors[j] = colorNames[j];
						flag = true;
					}
				}
				else
				{
					string firstColorFromLevel = Tile.ColorPalette.GetFirstColorFromLevel(j);
					if (m_colors[j] != firstColorFromLevel)
					{
						m_colors[j] = firstColorFromLevel;
						flag = true;
					}
				}
			}
			if (m_colors[j] == null)
			{
				m_colors[j] = "";
				flag = true;
			}
		}
		if (flag)
		{
			ColorsUpdated();
			if (syncUpdate && GameOwner == GameOwnerEnum.Server)
			{
				SyncColors();
			}
		}
		return true;
	}

	public void SyncColors()
	{
		string value = "";
		if (m_colors != null)
		{
			value = m_colors[0] + "|" + m_colors[1] + "|" + m_colors[2];
		}
		Properties.Get(ObjectPropertyID.Object_Script_Colors).Value = value;
	}

	public void SetColorsFrom(string colorValue)
	{
		string[] array = new string[3] { "", "", "" };
		if (!string.IsNullOrWhiteSpace(colorValue))
		{
			try
			{
				string[] array2 = colorValue.Split(new char[1] { '|' }, StringSplitOptions.None);
				if (array2 != null && array2.Length == 3)
				{
					array[0] = array2[0];
					array[1] = array2[1];
					array[2] = array2[2];
				}
			}
			catch
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, $"{GameOwner.ToString()}: Failed to parse colors {colorValue}");
			}
		}
		ApplyColors(array);
	}

	public string[] GetColors()
	{
		string[] array = new string[3];
		for (int i = 0; i < array.Length; i++)
		{
			if (m_colors != null && !string.IsNullOrEmpty(m_colors[i]))
			{
				array[i] = m_colors[i];
			}
			else if (Tile.ColorPalette != null)
			{
				array[i] = Tile.ColorPalette.GetFirstColorFromLevel(i);
			}
			if (array[i] == null)
			{
				array[i] = "";
			}
		}
		return array;
	}

	public void ColorsUpdated()
	{
		m_hasColors = m_colors != null && (!string.IsNullOrEmpty(m_colors[0]) || !string.IsNullOrEmpty(m_colors[1]) || !string.IsNullOrEmpty(m_colors[2]));
		if (Texture == null)
		{
			UpdateColorsInner(forceUncolored: true);
		}
		UpdateColorsQueued = true;
		if (GameWorld != null)
		{
			GameWorld.AddUpdateColorObject(this);
		}
	}

	public void UpdateColorsInner(bool forceUncolored = false)
	{
		List<ObjectDecal> list = new List<ObjectDecal>();
		if (m_objectDecals != null)
		{
			foreach (ObjectDecal objectDecal in m_objectDecals)
			{
				if (objectDecal.Texture == Texture)
				{
					list.Add(objectDecal);
				}
			}
		}
		if (!forceUncolored && HasColors && GameOwner != GameOwnerEnum.Server)
		{
			Texture = Textures.GetTexture(Tile.TextureName, m_colors);
		}
		else
		{
			Texture = Textures.GetTexture(Tile.TextureName);
		}
		TextureSolidColor = Textures.TextureIsSolidColor(Tile.TextureName);
		SetAnimationDataIfAvailable();
		foreach (ObjectDecal item in list)
		{
			item.Texture = Texture;
		}
		UpdateColorsQueued = false;
	}

	public bool CanSetColor(int level, string colorName)
	{
		if (Tile == null)
		{
			return false;
		}
		if (level >= 0 && level < Tile.ColorPalette.Colors.Length)
		{
			return Tile.ColorPalette.Colors[level].Contains(colorName);
		}
		return false;
	}

	public ObjectData(ObjectDataStartParams startParams)
		: this()
	{
		constructBase(startParams);
	}

	public ObjectData()
	{
		ScriptBridge = new ObjectDataScriptBridge(this);
		m_isTranslucencePointer = this is IObjectTranslucencePointer;
	}

	~ObjectData()
	{
	}

	public void constructBase(ObjectDataStartParams startParams)
	{
		GameOwner = startParams.GameOwner;
		LocalBox2DBodyCenterPoint = Microsoft.Xna.Framework.Vector2.Zero;
		Type = startParams.Type;
		ObjectID = startParams.ObjectID;
		CustomID = startParams.CustomID;
		MapObjectID = startParams.MapObjectID.ToUpperInvariant();
		m_groupID = startParams.GroupID;
		AllowDynamicPathNodeConnections = false;
		ProjectileTunnelingCheck = ProjectileTunnelingCheck.Full;
		SetTile(TileDatabase.Get(MapObjectID));
		m_owners = new List<Fixture>();
		m_objectDecals = new List<ObjectDecal>();
		InternalData = null;
		RemovalInitiated = false;
		DestructionInitiated = false;
		Activateable = false;
		ActivateRange = 0f;
		DrawFlag = false;
		Health = new BarMeter(1f, 1f);
		Destructable = false;
		DoDestroyObject = true;
		DoDraw = true;
		DrawOutsideCameraArea = false;
		DoExplosionHit = true;
		DoImpactHit = true;
		DoTakeExplosionDamage = true;
		DoTakeFireDamage = true;
		DoTakeImpactDamage = true;
		DoTakeProjectileDamage = true;
		DoTakePlayerImpactDamage = true;
		DoMergeProjectileHitsForStaticTiles = true;
		BreakOnDive = false;
		BreakOnStagger = false;
		ClientSyncDisableAnglePositionClippingCheck = false;
		ClientSyncEnableContactsOnDestroyedByPlayerImpact = false;
		InstaGibPlayer = false;
		LocalRenderLayer = -1;
		LocalDrawCategory = -1;
		FaceDirection = 1;
		PreservedJointsFromObjectID = 0;
		BotAIIgnoreObjectAsCover = false;
		ApplyColors(null);
		Properties = new ObjectProperties(this);
		Properties.Add(ObjectPropertyID.Object_CustomID);
		Properties.Add(ObjectPropertyID.Object_BodyType);
		if (Tile.Type == Tile.TYPE.Static)
		{
			Properties.Get(ObjectPropertyID.Object_BodyType).SetInitialValue(0);
		}
		else
		{
			Properties.Get(ObjectPropertyID.Object_BodyType).SetInitialValue(1);
		}
		if (Tile.Sizeable == Tile.SIZEABLE.V)
		{
			Properties.Add(ObjectPropertyID.Size_Y);
		}
		else if (Tile.Sizeable == Tile.SIZEABLE.H)
		{
			Properties.Add(ObjectPropertyID.Size_X);
		}
		else if (Tile.Sizeable == Tile.SIZEABLE.D)
		{
			Properties.Add(ObjectPropertyID.Size_X);
			Properties.Add(ObjectPropertyID.Size_Y);
		}
		Properties.Add(ObjectPropertyID.Object_BodyMass);
		Properties.Add(ObjectPropertyID.Object_StickyFeet);
		Properties.Add(ObjectPropertyID.Object_CollisionFilter);
		Properties.Add(ObjectPropertyID.Object_Script_Colors);
		SetProperties();
		Properties.FinishProperties();
	}

	public void ChangeBodyType(Box2D.XNA.BodyType type)
	{
		if (Body != null)
		{
			if (Body.GetType() == Box2D.XNA.BodyType.Dynamic && type == Box2D.XNA.BodyType.Static)
			{
				Body.SetType(Box2D.XNA.BodyType.Static);
				GameWorld.RemoveBodyToSync(BodyData);
				SyncTransform();
			}
			if (Body.GetType() == Box2D.XNA.BodyType.Static && type == Box2D.XNA.BodyType.Dynamic)
			{
				Body.SetType(Box2D.XNA.BodyType.Dynamic);
				GameWorld.AddBodyToSync(BodyData);
			}
		}
	}

	public SpawnObjectInformation.SpawnTypeValue GetCurrentSpawnType()
	{
		if (Body != null)
		{
			if (Body.GetType() == Box2D.XNA.BodyType.Dynamic)
			{
				return SpawnObjectInformation.SpawnTypeValue.Dynamic;
			}
			return SpawnObjectInformation.SpawnTypeValue.Static;
		}
		return SpawnObjectInformation.SpawnTypeValue.Default;
	}

	public AABB GetWorldAABB()
	{
		AABB aabb;
		if (Body != null)
		{
			Body.GetAABB(out aabb);
		}
		else
		{
			aabb = default(AABB);
		}
		aabb.lowerBound = Converter.Box2DToWorld(aabb.lowerBound);
		aabb.upperBound = Converter.Box2DToWorld(aabb.upperBound);
		return aabb;
	}

	public void SyncTransform()
	{
		Body.SetAwake(flag: true);
		if (GameOwner == GameOwnerEnum.Server && Body.GetType() != Box2D.XNA.BodyType.Dynamic)
		{
			GameWorld.AddSyncedPositionUpdate(BodyData);
		}
		CheckDrawFlag();
	}

	public void CheckDrawFlag()
	{
		if (DrawOutsideCameraArea)
		{
			DrawFlag = true;
		}
		else if (Body.GetType() == Box2D.XNA.BodyType.Static)
		{
			DrawFlag = DoDraw && GameWorld.CheckBodyInsideRenderingArea(Body);
		}
		else
		{
			DrawFlag = DoDraw;
		}
	}

	public float GetAngle()
	{
		return Body.GetAngle();
	}

	public Microsoft.Xna.Framework.Vector2 GetWorldPosition(Microsoft.Xna.Framework.Vector2 localWorldPoint)
	{
		return Converter.Box2DToWorld(Body.GetWorldPoint(Converter.WorldToBox2D(localWorldPoint)));
	}

	public Microsoft.Xna.Framework.Vector2 GetWorldPosition()
	{
		return Converter.Box2DToWorld(Body.GetPosition());
	}

	public Microsoft.Xna.Framework.Vector2 GetWorldCenterPosition()
	{
		return Converter.Box2DToWorld(GetBox2DCenterPosition());
	}

	public Microsoft.Xna.Framework.Vector2 GetWorldCenterPosition(bool simulateBox2DMissingTimestep)
	{
		if (simulateBox2DMissingTimestep)
		{
			return Converter.Box2DToWorld(GetBox2DCenterPosition() + GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetLinearVelocity());
		}
		return GetWorldCenterPosition();
	}

	public Microsoft.Xna.Framework.Vector2 GetBox2DPosition()
	{
		return Body.GetPosition();
	}

	public Microsoft.Xna.Framework.Vector2 GetLinearVelocity()
	{
		return Body.GetLinearVelocity();
	}

	public Microsoft.Xna.Framework.Vector2 GetBox2DCenterPosition()
	{
		return Body.GetWorldPoint(LocalBox2DBodyCenterPoint);
	}

	public virtual Microsoft.Xna.Framework.Vector2 GetWorldFocusPosition()
	{
		return GetWorldCenterPosition();
	}

	public ObjectData GetTranslucenceObject()
	{
		if (m_isTranslucencePointer)
		{
			return ((IObjectTranslucencePointer)this).GetTranslucenceObject();
		}
		return this;
	}

	public void AddToRenderLayer(int layer)
	{
		LocalRenderLayer = layer;
		LocalDrawCategory = Tile.DrawCategory;
		GameWorld.RenderCategories[Tile.DrawCategory].Add(this, LocalRenderLayer);
	}

	public void AddToRenderLayer(int layer, int drawCategory)
	{
		LocalRenderLayer = layer;
		LocalDrawCategory = drawCategory;
		GameWorld.RenderCategories[LocalDrawCategory].Add(this, LocalRenderLayer);
	}

	public void RemoveFromRenderLayer()
	{
		if (LocalRenderLayer != -1 && LocalDrawCategory != -1)
		{
			GameWorld.RenderCategories[LocalDrawCategory].Remove(this);
			LocalRenderLayer = -1;
			LocalDrawCategory = -1;
		}
	}

	public virtual void EditDrawExtraData(SpriteBatch spriteBatch)
	{
	}

	public void EditDrawExtraData(SpriteBatch spriteBatch, ObjectPropertyID targetObjectsProperty)
	{
		EditDrawExtraData(spriteBatch, targetObjectsProperty, Microsoft.Xna.Framework.Color.LightBlue);
	}

	public void EditDrawExtraData(SpriteBatch spriteBatch, ObjectPropertyID targetObjectsProperty, Microsoft.Xna.Framework.Color c)
	{
		List<ObjectData> targetNodes = GetTargetNodes(targetObjectsProperty);
		for (int i = 0; i < targetNodes.Count; i++)
		{
			GameWorld.DrawEditArrowLine(spriteBatch, this, targetNodes[i], c, 1.5f);
			GameWorld.EditHighlightObjectsOnce.Add(targetNodes[i]);
		}
	}

	public void EditDrawText(SpriteBatch spriteBatch, string text, Microsoft.Xna.Framework.Color c, int rowOffset = 0)
	{
		if (Constants.FontSimple != null)
		{
			float scale = Math.Min(Camera.Zoom, 1.2f);
			Constants.DrawString(spriteBatch, Constants.FontSimple, text, Camera.ConvertWorldToScreen(GetWorldCenterPosition() + new Microsoft.Xna.Framework.Vector2(4.2f, -3f * (float)rowOffset)), c, 0f, Microsoft.Xna.Framework.Vector2.Zero, scale, SpriteEffects.None, 0);
		}
	}

	public virtual void FinalizeProperties()
	{
	}

	public bool EditLayerSelectable()
	{
		if (Tile != null && Tile.DrawCategory != -1 && LocalRenderLayer != -1)
		{
			Layer<ObjectData> layer = GameWorld.RenderCategories[Tile.DrawCategory].GetLayer(LocalRenderLayer);
			if (!layer.IsLocked)
			{
				return layer.IsVisible;
			}
			return false;
		}
		return false;
	}

	public void Remove()
	{
		if (!RemovalInitiated)
		{
			RemovalInitiated = true;
			RemoveFromRenderLayer();
			GameWorld.AddCleanObject(this);
		}
	}

	public void Destroy()
	{
		if (!DestructionInitiated)
		{
			DestroyBeforeEventArgs e = new DestroyBeforeEventArgs();
			BeforeDestroyObject(e);
			if (!e.Cancel)
			{
				DestructionInitiated = true;
				GameWorld.AddCleanObject(this);
			}
		}
	}

	public void EnableUpdateObject()
	{
		if (!m_updateEnabled)
		{
			GameWorld.AddUpdateObject(this);
			m_updateEnabled = true;
		}
	}

	public void DisableUpdateObject()
	{
		if (m_updateEnabled)
		{
			GameWorld.RemoveUpdateObject(this);
			m_updateEnabled = false;
		}
	}

	public void SetGameWorld(GameWorld gameWorld)
	{
		GameWorld = gameWorld;
		CreateTime = gameWorld.ElapsedTotalGameTime;
		m_lastImpactTimestamp = gameWorld.ElapsedTotalGameTime;
		GameOwner = gameWorld.GameOwner;
		if (GameWorld != null)
		{
			GameWorld.AddUpdateColorObject(this);
		}
	}

	public virtual void Initialize()
	{
	}

	public void InitializeBase()
	{
		if (Tile.Sizeable != Tile.SIZEABLE.N)
		{
			FixedArray2<int> objectSizeMultiplier = GetObjectSizeMultiplier();
			RecreateFixture(objectSizeMultiplier[0], objectSizeMultiplier[1]);
		}
		else
		{
			AfterResizeFixture();
		}
		SetAnimationDataIfAvailable();
	}

	public void InitializeCustomID()
	{
		if (Properties.Exists(ObjectPropertyID.Object_CustomID))
		{
			string text = (string)Properties.Get(ObjectPropertyID.Object_CustomID).Value;
			if (text != "")
			{
				int value = 0;
				if (GameWorld.CustomIDTableLookup.TryGetValue(text, out value))
				{
					CustomID = value;
				}
				else
				{
					CustomID = 0;
				}
			}
			else
			{
				CustomID = 0;
			}
		}
		else
		{
			CustomID = 0;
		}
	}

	public void SetInitialBodyType(SpawnObjectInformation.SpawnTypeValue spawnType)
	{
		switch (spawnType)
		{
		case SpawnObjectInformation.SpawnTypeValue.Default:
			if (!Properties.Exists(ObjectPropertyID.Object_BodyType) || !(MapObjectID != "CONVERTTILETYPE"))
			{
				break;
			}
			switch ((int)Properties.Get(ObjectPropertyID.Object_BodyType).Value)
			{
			case 1:
				if (Body.GetType() != Box2D.XNA.BodyType.Dynamic)
				{
					Body.SetType(Box2D.XNA.BodyType.Dynamic);
					GameWorld.AddBodyToSync(BodyData);
				}
				break;
			case 0:
				if (Body.GetType() != Box2D.XNA.BodyType.Static)
				{
					Body.SetType(Box2D.XNA.BodyType.Static);
					GameWorld.RemoveBodyToSync(BodyData);
					SyncTransform();
				}
				break;
			}
			break;
		case SpawnObjectInformation.SpawnTypeValue.Static:
			if (Body.GetType() != Box2D.XNA.BodyType.Static)
			{
				Body.SetType(Box2D.XNA.BodyType.Static);
				GameWorld.RemoveBodyToSync(BodyData);
				SyncTransform();
			}
			break;
		case SpawnObjectInformation.SpawnTypeValue.Dynamic:
			if (Body.GetType() != Box2D.XNA.BodyType.Dynamic)
			{
				Body.SetType(Box2D.XNA.BodyType.Dynamic);
				GameWorld.AddBodyToSync(BodyData);
			}
			break;
		}
	}

	public void ClearDecals()
	{
		m_objectDecals.Clear();
	}

	public List<ObjectDecal> GetDecals()
	{
		return m_objectDecals;
	}

	public void AddDecal(ObjectDecal objectDecalToAdd)
	{
		m_objectDecals.Add(objectDecalToAdd);
	}

	public virtual void SetProperties()
	{
	}

	public virtual string HandleCustomProperty(ObjectPropertyInstance opi)
	{
		return "";
	}

	public void BasePropertyValueChangedCheckCommon(ObjectPropertyInstance propertyChanged, bool networkSilent)
	{
		if (Tile.Sizeable != Tile.SIZEABLE.N && (propertyChanged.Base.PropertyID == 19 || propertyChanged.Base.PropertyID == 20))
		{
			bool flag = false;
			int num = 1;
			int num2 = 1;
			if (Properties.Exists(ObjectPropertyID.Size_X))
			{
				num = (int)Properties.Get(ObjectPropertyID.Size_X).Value;
			}
			if (Properties.Exists(ObjectPropertyID.Size_Y))
			{
				num2 = (int)Properties.Get(ObjectPropertyID.Size_Y).Value;
			}
			if (m_lastSizeableWidth != num || m_lastSizeableHeight != num2)
			{
				flag = true;
				m_lastSizeableWidth = num;
				m_lastSizeableHeight = num2;
			}
			if (num < 1)
			{
				Properties.Get(ObjectPropertyID.Size_X).SetValue(1, networkSilent);
			}
			else if (num2 < 1)
			{
				Properties.Get(ObjectPropertyID.Size_Y).SetValue(1, networkSilent);
			}
			else if (flag)
			{
				RecreateFixture(num, num2);
			}
		}
		else if (propertyChanged.Base.PropertyID == 340)
		{
			UpdateStickyFeet_Internally((bool)propertyChanged.Value);
		}
		else if (propertyChanged.Base.PropertyID == 341)
		{
			UpdateBodyMass_Internally((float)propertyChanged.Value);
		}
		else if (propertyChanged.Base.PropertyID == 342)
		{
			string value = (string)propertyChanged.Value;
			if (!string.IsNullOrWhiteSpace(value))
			{
				UpdateCollisionFilter_Internally(value.ParseCollisionFilter());
			}
		}
		else if (propertyChanged.Base.PropertyID == 343 && GameOwner == GameOwnerEnum.Client)
		{
			SetColorsFrom((string)propertyChanged.Value);
		}
	}

	public CollisionFilter GetCollisionFilter()
	{
		CollisionFilter result = default(CollisionFilter);
		for (Fixture fixture = Body.GetFixtureList(); fixture != null; fixture = fixture.GetNext())
		{
			fixture.GetFilterData(out var filter);
			result.CategoryBits |= filter.categoryBits;
			result.MaskBits |= filter.maskBits;
			result.AboveBits |= filter.aboveBits;
			result.BlockMelee |= fixture.BlockMelee;
			result.ProjectileHit |= fixture.ProjectileHit;
			result.AbsorbProjectile |= fixture.AbsorbProjectile;
			result.BlockExplosions |= fixture.BlockExplosions;
			result.BlockFire |= fixture.BlockFire;
		}
		return result;
	}

	public void SetCollisionFilter(CollisionFilter value)
	{
		Properties.Get(ObjectPropertyID.Object_CollisionFilter).Value = value.Stringify();
	}

	public void UpdateCollisionFilter_Internally(CollisionFilter value)
	{
		if (!IsPlayer && Body != null)
		{
			for (Fixture fixture = Body.GetFixtureList(); fixture != null; fixture = fixture.GetNext())
			{
				fixture.GetFilterData(out var filter);
				filter.categoryBits = value.CategoryBits;
				filter.maskBits = value.MaskBits;
				filter.aboveBits = value.AboveBits;
				filter.blockMelee = value.BlockMelee;
				filter.projectileHit = value.ProjectileHit;
				filter.absorbProjectile = value.AbsorbProjectile;
				filter.blockExplosions = value.BlockExplosions;
				filter.blockFire = value.BlockFire;
				fixture.SetFilterData(ref filter);
			}
			Body.SetAwake(flag: true);
		}
	}

	public void EditFlipObject(short newFaceDirection)
	{
		if (FaceDirection != newFaceDirection)
		{
			FaceDirection = newFaceDirection;
			GetObjectSizeMultiplier(out var x, out var y);
			RecreateFixture(x, y);
		}
	}

	public void RecreateFixture(int widthX, int heightX)
	{
		if (Body == null)
		{
			return;
		}
		Dictionary<int, Filter> dictionary = new Dictionary<int, Filter>();
		foreach (Fixture owner in Owners)
		{
			owner.GetFilterData(out var filter);
			dictionary[owner.TileFixtureIndex] = filter;
			owner.SetUserData(null);
			Body.DestroyFixture(owner);
		}
		Owners.Clear();
		if (widthX < 1)
		{
			widthX = 1;
		}
		if (heightX < 1)
		{
			heightX = 1;
		}
		m_fixtureSizeXMultiplier = widthX;
		m_fixtureSizeYMultiplier = heightX;
		int num = Tile.Texture.Width;
		int num2 = Tile.Texture.Height;
		TileAnimation tileAnimation = Tile.GetTileAnimation(MapObjectID);
		if (tileAnimation != null)
		{
			num = tileAnimation.FrameWidth;
			num2 = tileAnimation.FrameHeight;
		}
		int num3 = widthX * num;
		int num4 = heightX * num2;
		if (!TextureSolidColor)
		{
			List<ObjectDecal> list = new List<ObjectDecal>(GetDecals().Count);
			foreach (ObjectDecal decal in GetDecals())
			{
				list.Add(decal);
			}
			Texture2D texture2D = null;
			texture2D = ((list.Count != 0) ? list[0].Texture : Textures.GetTexture(MapObjectID));
			ClearDecals();
			for (int i = 0; i < widthX; i++)
			{
				for (int j = 0; j < heightX; j++)
				{
					ObjectDecal objectDecal = null;
					if (list.Count > 0)
					{
						objectDecal = list[0];
						list.RemoveAt(0);
					}
					else
					{
						objectDecal = new ObjectDecal(texture2D);
					}
					objectDecal.LocalOffset = Converter.ConvertWorldToBox2D(new Microsoft.Xna.Framework.Vector2(i * num, 0f - (float)(j * num2)));
					objectDecal.HaveOffset = objectDecal.LocalOffset != Microsoft.Xna.Framework.Vector2.Zero;
					AddDecal(objectDecal);
				}
			}
			if (list.Count > 0)
			{
				foreach (ObjectDecal item in list)
				{
					item.Dispose();
				}
				list.Clear();
			}
		}
		else
		{
			m_solidColorOrigin = new Microsoft.Xna.Framework.Vector2((float)num * 0.5f, (float)num2 * 0.5f);
			m_solidColorOrigin.X = m_solidColorOrigin.X / (float)num3 * (float)num;
			m_solidColorOrigin.Y = m_solidColorOrigin.Y / (float)num4 * (float)num2;
			m_solidColorScale = new Microsoft.Xna.Framework.Vector2((float)num3 / (float)num * 1.0001f, (float)num4 / (float)num2 * 1.0001f);
		}
		for (int k = 0; k < Tile.TileFixtures.Count; k++)
		{
			Shape shape;
			try
			{
				if (Tile.Sizeable == Tile.SIZEABLE.D)
				{
					shape = new PolygonShape();
					float num5 = (float)num3 / 2f;
					float num6 = (float)num4 / 2f;
					LocalBox2DBodyCenterPoint = Converter.ConvertWorldToBox2D(new Microsoft.Xna.Framework.Vector2(num5 - (float)(num / 2), 0f - num6 + (float)(num2 / 2)));
					((PolygonShape)shape).SetAsBox(Converter.ConvertWorldToBox2D(num5), Converter.ConvertWorldToBox2D(num6), LocalBox2DBodyCenterPoint, 0f);
				}
				else if (Tile.Sizeable == Tile.SIZEABLE.H)
				{
					shape = new PolygonShape();
					Microsoft.Xna.Framework.Vector2[] indices = Tile.TileFixtures[k].GetIndices(FaceDirection);
					if (indices.Length != 4)
					{
						throw new NotImplementedException("ObjectData.RecreateFixture() can only handle fixtures with vertex count = 4");
					}
					int[] extremeIndicesIndex = Tile.TileFixtures[k].GetExtremeIndicesIndex(indices, TileFixture.IndicesIndexMode.Horizontal);
					indices[extremeIndicesIndex[0]].X = Converter.ConvertWorldToBox2D(0f - (float)num / 2f);
					indices[extremeIndicesIndex[1]].X = Converter.ConvertWorldToBox2D(0f - (float)num / 2f);
					indices[extremeIndicesIndex[2]].X = Converter.ConvertWorldToBox2D(0f - (float)num / 2f + (float)num3);
					indices[extremeIndicesIndex[3]].X = Converter.ConvertWorldToBox2D(0f - (float)num / 2f + (float)num3);
					LocalBox2DBodyCenterPoint = Converter.ConvertWorldToBox2D(new Microsoft.Xna.Framework.Vector2((0f - (float)num + (float)num3) / 2f, 0f));
					((PolygonShape)shape).Set(indices, indices.Length);
				}
				else if (Tile.Sizeable == Tile.SIZEABLE.V)
				{
					shape = new PolygonShape();
					Microsoft.Xna.Framework.Vector2[] indices2 = Tile.TileFixtures[k].GetIndices(FaceDirection);
					if (indices2.Length != 4)
					{
						throw new NotImplementedException("ObjectData.RecreateFixture() can only handle fixtures with vertex count = 4");
					}
					int[] extremeIndicesIndex2 = Tile.TileFixtures[k].GetExtremeIndicesIndex(indices2, TileFixture.IndicesIndexMode.Vertical);
					indices2[extremeIndicesIndex2[0]].Y = Converter.ConvertWorldToBox2D((float)num2 / 2f - (float)num4);
					indices2[extremeIndicesIndex2[1]].Y = Converter.ConvertWorldToBox2D((float)num2 / 2f - (float)num4);
					indices2[extremeIndicesIndex2[2]].Y = Converter.ConvertWorldToBox2D((float)num2 / 2f);
					indices2[extremeIndicesIndex2[3]].Y = Converter.ConvertWorldToBox2D((float)num2 / 2f);
					LocalBox2DBodyCenterPoint = Converter.ConvertWorldToBox2D(new Microsoft.Xna.Framework.Vector2(0f, (float)num2 / 2f - (float)num4) / 2f);
					((PolygonShape)shape).Set(indices2, indices2.Length);
				}
				else if (Tile.TileFixtures[k].CirclePoint == null)
				{
					try
					{
						shape = new PolygonShape();
						((PolygonShape)shape).Set(Tile.TileFixtures[k].GetIndices(FaceDirection), Tile.TileFixtures[k].Indices.Length);
					}
					catch (Exception ex)
					{
						throw new Exception("Error: Could not create tile " + Tile.ToString() + " make sure the vertices is in a CCW order\r\n" + ex.ToString());
					}
				}
				else
				{
					try
					{
						shape = Tile.TileFixtures[k].GetCircleShape(FaceDirection);
					}
					catch (Exception ex2)
					{
						throw new Exception("Error: Could not create tile " + Tile.ToString() + " make sure the vertices is in a CCW order\r\n" + ex2.ToString());
					}
				}
			}
			catch (Exception ex3)
			{
				throw new Exception("Error: Could not create tile " + Tile.ToString() + " make sure the vertices is in a CCW order\r\n" + ex3.ToString());
			}
			FixtureDef fixtureDef = new FixtureDef();
			Material material = ((Tile.TileFixtures[k].Material != null) ? Tile.TileFixtures[k].Material : Tile.Material);
			fixtureDef.restitution = material.Restitution;
			fixtureDef.friction = material.Friction;
			fixtureDef.density = material.Density;
			fixtureDef.filter = default(Filter);
			fixtureDef.filter.cloudRotation = ((FaceDirection == 1) ? Tile.TileFixtures[k].Filter.box2DFilter.cloudRotation : (0f - Tile.TileFixtures[k].Filter.box2DFilter.cloudRotation));
			if (dictionary.TryGetValue(k, out var value))
			{
				fixtureDef.filter.categoryBits = value.categoryBits;
				fixtureDef.filter.maskBits = value.maskBits;
				fixtureDef.filter.groupIndex = value.groupIndex;
				fixtureDef.filter.aboveBits = value.aboveBits;
				fixtureDef.filter.isCloud = value.isCloud;
				fixtureDef.filter.kickable = value.kickable;
				fixtureDef.filter.kickableTop = value.kickableTop;
				fixtureDef.filter.punchable = value.punchable;
				fixtureDef.filter.blockMelee = value.blockMelee;
				fixtureDef.filter.blockFire = value.blockFire;
				fixtureDef.filter.blockExplosions = value.blockExplosions;
				fixtureDef.filter.projectileHit = value.projectileHit;
				fixtureDef.filter.absorbProjectile = value.absorbProjectile;
				fixtureDef.filter.objectStrength = value.objectStrength;
			}
			else
			{
				fixtureDef.filter.categoryBits = Tile.TileFixtures[k].Filter.box2DFilter.categoryBits;
				fixtureDef.filter.maskBits = Tile.TileFixtures[k].Filter.box2DFilter.maskBits;
				fixtureDef.filter.groupIndex = Tile.TileFixtures[k].Filter.box2DFilter.groupIndex;
				fixtureDef.filter.aboveBits = Tile.TileFixtures[k].Filter.box2DFilter.aboveBits;
				fixtureDef.filter.isCloud = Tile.TileFixtures[k].Filter.box2DFilter.isCloud;
				fixtureDef.filter.kickable = Tile.Kickable & Tile.TileFixtures[k].Filter.box2DFilter.kickable;
				fixtureDef.filter.kickableTop = Tile.KickableTop & Tile.TileFixtures[k].Filter.box2DFilter.kickableTop;
				fixtureDef.filter.punchable = Tile.Punchable & Tile.TileFixtures[k].Filter.box2DFilter.punchable;
				fixtureDef.filter.blockMelee = Tile.Punchable & Tile.TileFixtures[k].Filter.box2DFilter.blockMelee;
				fixtureDef.filter.blockFire = Tile.TileFixtures[k].Filter.box2DFilter.blockFire;
				fixtureDef.filter.blockExplosions = material.BlockExplosions;
				fixtureDef.filter.projectileHit = Tile.TileFixtures[k].ProjectileHit;
				fixtureDef.filter.absorbProjectile = Tile.TileFixtures[k].AbsorbProjectile;
				fixtureDef.filter.objectStrength = Tile.TileFixtures[k].ObjectStrength;
			}
			fixtureDef.shape = shape;
			Fixture fixture = Body.CreateFixture(fixtureDef);
			if (Tile.TileFixtures[k].Filter.mass != 0f)
			{
				fixture.SetMass(Tile.TileFixtures[k].Filter.mass);
			}
			fixture.ID = Tile.TileFixtures[k].ID;
			fixture.TileFixtureIndex = (byte)k;
			fixture.SetUserData(this);
			AddOwner(fixture);
		}
		AfterResizeFixture();
	}

	public virtual void AfterResizeFixture()
	{
	}

	public virtual void PropertyValueChanged(ObjectPropertyInstance propertyChanged)
	{
	}

	public void BasePropertyValueChanged(ObjectPropertyInstance propertyChanged, bool networkSilent)
	{
		if (!networkSilent && GameOwner == GameOwnerEnum.Server && (propertyChanged.Base.SyncType == ObjectPropertySyncType.SyncedTCP || propertyChanged.Base.SyncType == ObjectPropertySyncType.SyncedUDP) && GameSFD.Handle.Server != null && GameSFD.Handle.Server.NetServer != null)
		{
			GameWorld.AddObjectPropertyValueToSend(propertyChanged);
		}
		BasePropertyValueChangedCheckCommon(propertyChanged, networkSilent);
		PropertyValueChanged(propertyChanged);
	}

	public virtual void TakeImpactDamage(ref float damage, DamageType damageType, int sourceID)
	{
		if (Destructable && damage >= Tile.Material.Resistance.Impact.Threshold && DoTakeImpactDamage)
		{
			damage *= Tile.Material.Resistance.Impact.Modifier;
			DoTakeDamage(damage, damageType, sourceID);
			if (m_health.IsEmpty && GameOwner == GameOwnerEnum.Client)
			{
				GameWorld.AddClientHealthCheck(this);
			}
		}
		else
		{
			damage = 0f;
		}
	}

	public void DoTakeDamage(float damage, DamageType damageType, int sourceID)
	{
		m_health.CurrentValue -= damage;
		if (!GameWorld.ScriptCallbackExists_ObjectDamage || GameOwner == GameOwnerEnum.Client)
		{
			return;
		}
		switch (damageType)
		{
		case DamageType.OtherScripts:
			if (!RunOnDamageOtherScriptTypeActive)
			{
				RunOnDamageOtherScriptTypeActive = true;
				GameWorld.RunScriptOnObjectDamageCallbacks(this, damageType, damage, sourceID);
				RunOnDamageOtherScriptTypeActive = false;
			}
			break;
		default:
			GameWorld.RunScriptOnObjectDamageCallbacks(this, damageType, damage, sourceID);
			break;
		case DamageType.Impact:
		case DamageType.PlayerImpact:
			GameWorld.QueueRunScriptOnObjectDamageCallbacks(this, damageType, damage, sourceID);
			break;
		}
	}

	public virtual void TakePlayerImpactDamage(float damage, int playerID)
	{
		if (Destructable && damage >= Tile.Material.Resistance.PlayerImpact.Threshold && DoTakePlayerImpactDamage)
		{
			DoTakeDamage(damage * Tile.Material.Resistance.PlayerImpact.Modifier, DamageType.PlayerImpact, playerID);
			if (m_health.IsEmpty && GameOwner == GameOwnerEnum.Client)
			{
				GameWorld.AddClientHealthCheck(this);
			}
		}
	}

	public virtual void TakeProjectileDamage(Projectile projectile)
	{
		Material tileFixtureMaterial = Tile.GetTileFixtureMaterial(projectile.HitFixtureIndex);
		if (Destructable && projectile.Properties.ObjectDamage >= tileFixtureMaterial.Resistance.Projectile.Threshold && DoTakeProjectileDamage)
		{
			float damage = (projectile.HitDamageValue = projectile.Properties.ObjectDamage * tileFixtureMaterial.Resistance.Projectile.Modifier);
			DoTakeDamage(damage, DamageType.Projectile, projectile.InstanceID);
			if (m_health.IsEmpty && GameOwner == GameOwnerEnum.Client)
			{
				GameWorld.AddClientHealthCheck(this);
			}
		}
	}

	public virtual void TakeExplosionDamage(ref float damage, int explosionID)
	{
		if (Destructable && damage >= Tile.Material.Resistance.Explosion.Threshold && DoTakeExplosionDamage && TotalExistTime > 150f)
		{
			damage *= Tile.Material.Resistance.Explosion.Modifier;
			DoTakeDamage(damage, DamageType.Explosion, explosionID);
			if (m_health.IsEmpty && GameOwner == GameOwnerEnum.Client)
			{
				GameWorld.AddClientHealthCheck(this);
			}
		}
		else
		{
			damage = 0f;
		}
	}

	public virtual void TakeFireDamage(float damage, float igniteValue)
	{
		if (!Fire.IsBurning)
		{
			if (CanBurn || CanTakeFireDamage)
			{
				Fire.IgnitionValue += igniteValue;
			}
			if (Fire.IgnitionValue > Tile.Material.Resistance.Fire.Threshold)
			{
				Fire.IgnitionValue = Tile.Material.Resistance.Fire.Threshold;
				if (CanBurn)
				{
					Fire.BurnTime = 5000f;
				}
				if (CanTakeFireDamage)
				{
					DoTakeDamage(damage * Tile.Material.Resistance.Fire.Modifier, DamageType.Fire, 0);
				}
			}
			if (Fire.IgnitionValue >= 5f)
			{
				Fire.SmokeTime = 3000f;
			}
		}
		else if (CanTakeFireDamage)
		{
			DoTakeDamage(damage * Tile.Material.Resistance.Fire.Modifier, DamageType.Fire, 0);
		}
	}

	public void StartTrackingFireValues()
	{
		if (!TrackingFireValues)
		{
			GameWorld.FireGrid.StartTrackFireValues(this);
		}
	}

	public void SetSmokeTime(float time)
	{
		if (CanTakeFireDamage || CanBurn)
		{
			Fire.SmokeTime = 3000f;
			StartTrackingFireValues();
		}
	}

	public void ForceSetSmokeTime(float time)
	{
		Fire.SmokeTime = 3000f;
		StartTrackingFireValues();
	}

	public void StopTrackingFireValues()
	{
		if (TrackingFireValues)
		{
			GameWorld.FireGrid.StopTrackFireValues(this);
		}
	}

	public virtual void DrawUpdate(float ms)
	{
		if (CurrentAnimation != null && !CurrentAnimation.IsSynced)
		{
			CurrentAnimation.Progress(ms);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void GetDrawColor(ref Microsoft.Xna.Framework.Color drawColor)
	{
		if (Health.CheckRecentlyDecreased(40f) && TotalExistTime > 300f)
		{
			drawColor = ColorCorrection.CreateCustom(Constants.COLORS.DAMAGE_FLASH_PLAYER) * 2f;
		}
	}

	public virtual void Draw(SpriteBatch spriteBatch, float ms)
	{
		DrawBase(spriteBatch, ms, Health.DrawColor);
	}

	public void Draw(SpriteBatch spriteBatch, float ms, Microsoft.Xna.Framework.Color color)
	{
		DrawBase(spriteBatch, ms, color);
	}

	public void DrawBase(SpriteBatch spriteBatch, float ms, Microsoft.Xna.Framework.Color drawColor)
	{
		if (TextureSolidColor)
		{
			ObjectDecal objectDecal = m_objectDecals[0];
			Microsoft.Xna.Framework.Vector2 box2DCoordinate = (objectDecal.HaveOffset ? Body.GetWorldPoint(objectDecal.LocalOffset) : Body.Position);
			box2DCoordinate += GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetLinearVelocity();
			Camera.ConvertBox2DToScreen(ref box2DCoordinate, out box2DCoordinate);
			float rotation = 0f - Body.GetAngle() - GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetAngularVelocity();
			spriteBatch.Draw(objectDecal.Texture, box2DCoordinate, null, Microsoft.Xna.Framework.Color.Gray, rotation, m_solidColorOrigin, Camera.Zoom * m_solidColorScale, m_faceDirectionSpriteEffect, 0f);
		}
		else if (CurrentAnimation != null)
		{
			if ((m_fixtureSizeXMultiplier != 1) | (m_fixtureSizeYMultiplier != 1))
			{
				Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(Body.GetPosition());
				Microsoft.Xna.Framework.Vector2 position = Microsoft.Xna.Framework.Vector2.Zero;
				for (int i = 0; i < m_fixtureSizeXMultiplier; i++)
				{
					for (int j = 0; j < m_fixtureSizeYMultiplier; j++)
					{
						position.X = i * CurrentAnimation.FrameWidth;
						position.Y = 0f - (float)(j * CurrentAnimation.FrameHeight);
						SFDMath.RotatePosition(ref position, Body.GetAngle(), out position);
						Microsoft.Xna.Framework.Vector2 box2DCoordinate = Camera.ConvertWorldToScreen(vector + position);
						CurrentAnimation.Draw(spriteBatch, Texture, box2DCoordinate, Body.GetAngle(), m_faceDirectionSpriteEffect);
					}
				}
			}
			else
			{
				Microsoft.Xna.Framework.Vector2 box2DCoordinate = Body.GetPosition();
				Camera.ConvertBox2DToScreen(ref box2DCoordinate, out box2DCoordinate);
				CurrentAnimation.Draw(spriteBatch, Texture, box2DCoordinate, Body.GetAngle(), m_faceDirectionSpriteEffect);
			}
		}
		else
		{
			float num = 0f - Body.GetAngle();
			ObjectDecal objectDecal2 = null;
			if (m_objectDecals.Count == 1)
			{
				num -= GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetAngularVelocity();
			}
			GetDrawColor(ref drawColor);
			for (int k = 0; k < m_objectDecals.Count; k++)
			{
				objectDecal2 = m_objectDecals[k];
				Microsoft.Xna.Framework.Vector2 box2DCoordinate = (objectDecal2.HaveOffset ? Body.GetWorldPoint(objectDecal2.LocalOffset) : Body.Position);
				box2DCoordinate += GameWorld.DrawingBox2DSimulationTimestepOver * Body.GetLinearVelocity();
				Camera.ConvertBox2DToScreen(ref box2DCoordinate, out box2DCoordinate);
				spriteBatch.Draw(objectDecal2.Texture, box2DCoordinate, null, drawColor, num, objectDecal2.TextureOrigin, Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
			}
		}
	}

	public virtual void BeforeImpactHit(ObjectData otherObject, ImpactBeforeHitEventArgs e)
	{
	}

	public virtual void ImpactHit(ObjectData otherObject, ImpactHitEventArgs e)
	{
		ObjectDataMethods.DefaultImpactHit(this, otherObject, e);
		if (GameOwner != GameOwnerEnum.Client && Health.Fullness <= 0f && Tile.Material.Resistance.Impact.Modifier > 0f)
		{
			Destroy();
		}
	}

	public virtual void BeforePlayerMeleeHit(Player player, PlayerBeforeHitEventArgs e)
	{
	}

	public virtual void PlayerMeleeHit(Player player, PlayerHitEventArgs e)
	{
		ObjectDataMethods.DefaultPlayerHit(this, player, e);
	}

	public void ObjectIDCompression(Dictionary<int, int> oldToNewValues, Dictionary<ObjectPropertyInstance, object> oldValueIDs)
	{
		bool flag = true;
		foreach (ObjectPropertyInstance item in Properties.Items)
		{
			if (item.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
			{
				oldValueIDs.Add(item, item.Value);
				int num = (int)item.Value;
				if (oldToNewValues.ContainsKey(num))
				{
					num = oldToNewValues[num];
					item.BaseSetValue(num);
				}
				else if (num != 0 && flag)
				{
					item.BaseSetValue(0);
				}
			}
			if (item.Base.PropertyClass != ObjectPropertyClass.TargetObjectDataMultiple)
			{
				continue;
			}
			oldValueIDs.Add(item, item.Value);
			int[] array = Converter.StringToIntArray((string)item.Value);
			List<int> list = new List<int>();
			int[] array2 = array;
			foreach (int num2 in array2)
			{
				if (oldToNewValues.ContainsKey(num2))
				{
					list.Add(oldToNewValues[num2]);
				}
				else if (!flag)
				{
					list.Add(num2);
				}
			}
			string value = Converter.IntArrayToString(list.ToArray());
			item.BaseSetValue(value);
		}
	}

	public virtual void ObjectIDUnCompression(Dictionary<ObjectPropertyInstance, object> oldValueIDs)
	{
		foreach (ObjectPropertyInstance item in Properties.Items)
		{
			if (oldValueIDs.ContainsKey(item))
			{
				item.BaseSetValue(oldValueIDs[item]);
			}
		}
	}

	public List<ObjectData> GetTargetNodes(ObjectPropertyID propertyID)
	{
		return GetObjectsFromProperty(propertyID);
	}

	public virtual void SensorBeginContact(Contact contact, Fixture fixture, Fixture otherFixture)
	{
	}

	public virtual void SensorEndContact(Contact contact, Fixture fixture, Fixture otherFixture)
	{
	}

	public virtual void SolveImpactContactHit(Contact contact, ObjectData otherObject, Fixture myFixture, Fixture otherFixture, ref WorldManifold worldManifold, ref FixedArray2<PointState> pointStates, ref Manifold manifold)
	{
		if (!DoImpactHit || (otherObject.IsPlayer | IsPlayer))
		{
			return;
		}
		Body body = Body;
		Body body2 = otherObject.Body;
		bool flag = body.GetType() == Box2D.XNA.BodyType.Dynamic && body2.GetType() == Box2D.XNA.BodyType.Dynamic;
		for (int i = 0; i <= 1; i++)
		{
			if (pointStates[i] != PointState.Add)
			{
				continue;
			}
			Microsoft.Xna.Framework.Vector2 worldPoint = worldManifold._points[i];
			Microsoft.Xna.Framework.Vector2 v = worldManifold._normal;
			FixedArray2<Microsoft.Xna.Framework.Vector2> impactLinearVelocityFromWorldPoint = body.GetImpactLinearVelocityFromWorldPoint(body2, worldPoint);
			float num = ((body.GetType() == Box2D.XNA.BodyType.Static) ? 0f : (body.GetMass() * impactLinearVelocityFromWorldPoint[0].Length()));
			float num2 = ((body2.GetType() == Box2D.XNA.BodyType.Static) ? 0f : (body2.GetMass() * impactLinearVelocityFromWorldPoint[1].Length()));
			float num3 = num / (num + num2);
			float val = body.GetMass() * num3 + body2.GetMass() * (1f - num3);
			val = Math.Min(val, (body2.GetType() == Box2D.XNA.BodyType.Static) ? body2.GetStaticMass() : body2.GetMass());
			Microsoft.Xna.Framework.Vector2 u = impactLinearVelocityFromWorldPoint[0] - impactLinearVelocityFromWorldPoint[1];
			SFDMath.ProjectUonV(ref u, ref v, out u);
			float num4 = u.Length() - ImpactTresholdValue;
			if (!(num4 > 0f))
			{
				continue;
			}
			float num5 = Converter.ConvertMassBox2DToKG(val) * 0.04f * num4 * (flag ? 0.5f : 1f) - ImpactDamageTresholdValue;
			if (num5 > 0f)
			{
				ImpactBeforeHitEventArgs e = new ImpactBeforeHitEventArgs(myFixture, otherFixture, worldPoint, v, num5, ImpactHitCause.Box2DSimulation);
				BeforeImpactHit(otherObject, e);
				if (e.Cancel)
				{
					break;
				}
				ImpactHitEventArgs e2 = new ImpactHitEventArgs(myFixture, otherFixture, worldPoint, v, num5, ImpactHitCause.Box2DSimulation);
				if (GameOwner != GameOwnerEnum.Server)
				{
					PlayImpactEffect(e2);
				}
				ImpactHit(otherObject, e2);
			}
		}
	}

	public virtual void ProjectileHit(Projectile projectile, ProjectileHitEventArgs e)
	{
		ObjectDataMethods.DefaultProjectileHit(this, projectile, e);
		if (GameOwner != GameOwnerEnum.Client && Health.Fullness <= 0f && Tile.Material.Resistance.Projectile.Modifier > 0f)
		{
			Destroy();
		}
	}

	public virtual void BeforeExplosionHit(Explosion explosionData, ExplosionBeforeHitEventArgs e)
	{
	}

	public virtual void ExplosionHit(Explosion explosionData, ExplosionHitEventArgs e)
	{
		ObjectDataMethods.DefaultExplosionHit(this, explosionData, e);
		if (GameOwner != GameOwnerEnum.Client && Health.Fullness <= 0f && Tile.Material.Resistance.Explosion.Modifier > 0f)
		{
			Destroy();
		}
	}

	public virtual void BeforeMissileHitObject(ObjectData od, MissileBeforeHitEventArgs e)
	{
	}

	public virtual void MissileHitPlayer(Player player, MissileHitEventArgs e)
	{
		ObjectDataMethods.DefaultMissileHitPlayer(this, player, e);
	}

	public virtual float GetMissileHitPlayerDamage(Player player)
	{
		return ObjectDataMethods.DefaultGetMissileHitObjectDamage(this, player.ObjectData);
	}

	public virtual void DealScriptDamage(float damage, int sourceID = 0)
	{
		if (Destructable && damage > 0f)
		{
			DoTakeDamage(damage, DamageType.OtherScripts, sourceID);
			if (GameOwner != GameOwnerEnum.Client && Health.Fullness <= 0f)
			{
				Destroy();
			}
		}
	}

	public virtual void OnDestroyObject()
	{
	}

	public void OnDestroyGenericCheck()
	{
		if (GameOwner != GameOwnerEnum.Server)
		{
			if (!string.IsNullOrEmpty(Tile.Material.DestroyEffect))
			{
				EffectHandler.PlayEffect(Tile.Material.DestroyEffect, GetWorldPosition(), GameWorld);
			}
			if (!string.IsNullOrEmpty(Tile.Material.DestroyEffect))
			{
				SoundHandler.PlaySound(Tile.Material.DestroySound, GameWorld);
			}
		}
	}

	public virtual void OnRemoveObject()
	{
	}

	public virtual void BeforeDestroyObject(DestroyBeforeEventArgs e)
	{
	}

	public virtual void UpdateObject(float ms)
	{
	}

	public virtual void UpdateObjectBeforeBox2DStep(float timestep)
	{
	}

	public virtual void Activate(ObjectData sender)
	{
	}

	public virtual ObjectData GetActivateableHighlightObject(Player requestingPlayer)
	{
		return this;
	}

	public virtual void EditBeforeSave()
	{
	}

	public virtual void EditAfterSave()
	{
	}

	public virtual void EditAfterHistoryRecreated()
	{
	}

	public virtual void Dispose()
	{
		if (m_isDisposed)
		{
			return;
		}
		m_isDisposed = true;
		if (GameWorld != null)
		{
			if (ScriptBridge != null)
			{
				GameWorld.QueueScriptBridgeDispose(ScriptBridge);
			}
			SetGroupID(0);
			DisableUpdateObject();
			GameWorld.RemoveMissileObject(this);
			GameWorld.RemoveUpdateObject(this);
			GameWorld.RemoveBodyToSync(BodyData);
			GameWorld.ObjectSyncedPositionUpdates.Remove(BodyData);
			IsAITargetableObject = false;
			RemoveFromRenderLayer();
			if (BodyData != null)
			{
				GameWorld.RemoveBodyToSync(BodyData);
				GameWorld.DynamicBodies.Remove(BodyData.BodyID);
				GameWorld.StaticBodies.Remove(BodyData.BodyID);
				BodyData.Dispose();
				BodyData = null;
			}
			GameWorld.DynamicObjects.Remove(ObjectID);
			GameWorld.StaticObjects.Remove(ObjectID);
			if (m_body != null)
			{
				m_body.SetUserData(null);
				if (!m_body.IsDestroyed)
				{
					m_body.GetWorld().DestroyBody(m_body);
				}
				m_body = null;
			}
		}
		else if (ScriptBridge != null)
		{
			ScriptBridge.Dispose();
			ScriptBridge = null;
		}
		if (Properties != null)
		{
			Properties.Dispose();
			Properties = null;
		}
		if (Type == Tile.TYPE.Player && InternalData != null)
		{
			((Player)InternalData).WorldBody = null;
		}
		MissileData = null;
		Texture = null;
		m_colors = null;
		m_totalGibbFramePressures = null;
		if (InternalData is Player)
		{
			((Player)InternalData).Dispose();
		}
		InternalData = null;
		CustomData = null;
		Tile = null;
		if (m_owners != null)
		{
			m_owners.Clear();
			m_owners = null;
		}
		if (m_objectDecals != null)
		{
			ClearDecals();
			m_objectDecals = null;
		}
		if (m_syncedMethodCounter != null)
		{
			m_syncedMethodCounter.Clear();
			m_syncedMethodCounter = null;
		}
		m_health = null;
		GameWorld = null;
		CurrentAnimation = null;
	}

	public Rectangle GetOccupyingWorldArea()
	{
		Rectangle result = default(Rectangle);
		if (Body != null)
		{
			Body.GetAABB(out var aabb);
			result.X = (int)Math.Round(Converter.ConvertBox2DToWorld(aabb.lowerBound.X));
			result.Y = (int)Math.Round(Converter.ConvertBox2DToWorld(aabb.upperBound.Y));
			result.Width = (int)Math.Round(Converter.ConvertBox2DToWorld(aabb.upperBound.X)) - result.X;
			result.Height = (int)Math.Round(Converter.ConvertBox2DToWorld(aabb.lowerBound.Y)) - result.Y;
		}
		return result;
	}

	public virtual bool EditCheckTouch(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		return DefaultEditCheckTouch(worldPosition);
	}

	public virtual bool EditCheckTouch(GameWorld.SelectionArea area)
	{
		return DefaultEditCheckTouch(area);
	}

	public bool GetSizeablePosition(Tile.SIZEABLE size, out Microsoft.Xna.Framework.Vector2 result)
	{
		int num = 1;
		int num2 = 1;
		if (Properties.Exists(ObjectPropertyID.Size_X))
		{
			num = (int)Properties.Get(ObjectPropertyID.Size_X).Value;
		}
		if (Properties.Exists(ObjectPropertyID.Size_Y))
		{
			num2 = (int)Properties.Get(ObjectPropertyID.Size_Y).Value;
		}
		FixedArray2<int> textureSize = GetTextureSize();
		num *= textureSize[0];
		num2 *= textureSize[1];
		switch (size)
		{
		case Tile.SIZEABLE.V:
			if (Tile.Sizeable == Tile.SIZEABLE.V || Tile.Sizeable == Tile.SIZEABLE.D)
			{
				Microsoft.Xna.Framework.Vector2 localWorldPoint2 = new Microsoft.Xna.Framework.Vector2(num / 2, -num2) + new Microsoft.Xna.Framework.Vector2(-textureSize[0] / 2, textureSize[1] / 2);
				result = GetWorldPosition(localWorldPoint2);
				return true;
			}
			break;
		case Tile.SIZEABLE.H:
			if (Tile.Sizeable == Tile.SIZEABLE.H || Tile.Sizeable == Tile.SIZEABLE.D)
			{
				Microsoft.Xna.Framework.Vector2 localWorldPoint3 = new Microsoft.Xna.Framework.Vector2(num, -num2 / 2) + new Microsoft.Xna.Framework.Vector2(-textureSize[0] / 2, textureSize[1] / 2);
				result = GetWorldPosition(localWorldPoint3);
				return true;
			}
			break;
		case Tile.SIZEABLE.D:
			if (Tile.Sizeable == Tile.SIZEABLE.D)
			{
				Microsoft.Xna.Framework.Vector2 localWorldPoint = new Microsoft.Xna.Framework.Vector2(num, -num2) + new Microsoft.Xna.Framework.Vector2(-textureSize[0] / 2, textureSize[1] / 2);
				result = GetWorldPosition(localWorldPoint);
				return true;
			}
			break;
		}
		result = Microsoft.Xna.Framework.Vector2.Zero;
		return false;
	}

	public FixedArray2<int> GetTextureSize()
	{
		FixedArray2<int> result = default(FixedArray2<int>);
		TileAnimation tileAnimation = Tile.GetTileAnimation(MapObjectID);
		if (tileAnimation != null)
		{
			result[0] = Math.Min(tileAnimation.FrameWidth, Tile.Texture.Width);
			result[1] = Math.Min(tileAnimation.FrameHeight, Tile.Texture.Height);
		}
		else
		{
			result[0] = Tile.Texture.Width;
			result[1] = Tile.Texture.Height;
		}
		return result;
	}

	public Area GetObjectArea()
	{
		GetObjectSize(out var widthX, out var heightY);
		Area area = new Area();
		Microsoft.Xna.Framework.Vector2 worldPosition = GetWorldPosition();
		FixedArray2<int> textureSize = GetTextureSize();
		area.Left = worldPosition.X - (float)textureSize[0] * 0.5f;
		area.Right = area.Left + (float)widthX;
		area.Top = worldPosition.Y + (float)textureSize[1] * 0.5f;
		area.Bottom = area.Top - (float)heightY;
		return area;
	}

	public ItemContainer<int, int> GetObjectSize()
	{
		int num = 1;
		int num2 = 1;
		if (Properties.Exists(ObjectPropertyID.Size_X))
		{
			num = (int)Properties.Get(ObjectPropertyID.Size_X).Value;
		}
		if (Properties.Exists(ObjectPropertyID.Size_Y))
		{
			num2 = (int)Properties.Get(ObjectPropertyID.Size_Y).Value;
		}
		if (CurrentAnimation != null)
		{
			num *= CurrentAnimation.FrameWidth;
			num2 *= CurrentAnimation.FrameHeight;
		}
		else
		{
			num *= Tile.Texture.Width;
			num2 *= Tile.Texture.Height;
		}
		return new ItemContainer<int, int>(num, num2);
	}

	public FixedArray2<int> GetObjectSizeMultiplier()
	{
		int value = 1;
		int value2 = 1;
		if (Properties.Exists(ObjectPropertyID.Size_X))
		{
			value = (int)Properties.Get(ObjectPropertyID.Size_X).Value;
		}
		if (Properties.Exists(ObjectPropertyID.Size_Y))
		{
			value2 = (int)Properties.Get(ObjectPropertyID.Size_Y).Value;
		}
		return new FixedArray2<int>
		{
			[0] = value,
			[1] = value2
		};
	}

	public void SetObjectSizeMultiplier(int widthX, int heightY)
	{
		if (Properties.Exists(ObjectPropertyID.Size_X))
		{
			Properties.Get(ObjectPropertyID.Size_X).Value = widthX;
		}
		if (Properties.Exists(ObjectPropertyID.Size_Y))
		{
			Properties.Get(ObjectPropertyID.Size_Y).Value = heightY;
		}
	}

	public void GetObjectSizeMultiplier(out int x, out int y)
	{
		x = m_fixtureSizeXMultiplier;
		y = m_fixtureSizeYMultiplier;
	}

	public void GetObjectSize(out int widthX, out int heightY)
	{
		widthX = m_fixtureSizeXMultiplier;
		heightY = m_fixtureSizeYMultiplier;
		if (CurrentAnimation != null)
		{
			widthX *= CurrentAnimation.FrameWidth;
			heightY *= CurrentAnimation.FrameHeight;
		}
		else
		{
			widthX *= Tile.Texture.Width;
			heightY *= Tile.Texture.Height;
		}
	}

	public void GetObjectBaseSize(out int widthX, out int heightY)
	{
		if (CurrentAnimation != null)
		{
			widthX = CurrentAnimation.FrameWidth;
			heightY = CurrentAnimation.FrameHeight;
		}
		else
		{
			widthX = Tile.Texture.Width;
			heightY = Tile.Texture.Height;
		}
	}

	public ObjectData GetObjectFromProperty(ObjectPropertyID propertyID, bool handleTranslucensePointer = false)
	{
		return GetObjectFromProperty<ObjectData>(propertyID, handleTranslucensePointer);
	}

	public T GetObjectFromProperty<T>(ObjectPropertyID propertyID, bool handleTranslucensePointer = false) where T : ObjectData
	{
		List<T> objectsFromProperty = GetObjectsFromProperty<T>(propertyID, handleTranslucensePointer);
		if (objectsFromProperty.Count >= 1)
		{
			return objectsFromProperty[0];
		}
		return null;
	}

	public List<ObjectData> GetObjectsFromProperty(ObjectPropertyID propertyID, bool handleTranslucensePointer = false)
	{
		return GetObjectsFromProperty<ObjectData>(propertyID, handleTranslucensePointer);
	}

	public List<T> GetObjectsFromProperty<T>(ObjectPropertyID propertyID, bool handleTranslucensePointer = false) where T : ObjectData
	{
		List<T> list = new List<T>();
		if (Properties.Exists(propertyID))
		{
			ObjectPropertyInstance objectPropertyInstance = Properties.Get(propertyID);
			if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectDataMultiple)
			{
				int[] array = Converter.StringToIntArray((string)objectPropertyInstance.Value);
				foreach (int iD in array)
				{
					ObjectData objectDataByID = GameWorld.GetObjectDataByID(iD);
					if (objectDataByID == null || objectDataByID.IsDisposed)
					{
						continue;
					}
					if (handleTranslucensePointer)
					{
						objectDataByID = objectDataByID.GetTranslucenceObject();
						if (objectDataByID != null && !objectDataByID.IsDisposed && objectDataByID is T)
						{
							list.Add((T)objectDataByID);
						}
					}
					else if (objectDataByID is T)
					{
						list.Add((T)objectDataByID);
					}
				}
			}
			else if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
			{
				ObjectData objectDataByID2 = GameWorld.GetObjectDataByID((int)objectPropertyInstance.Value);
				if (objectDataByID2 != null && !objectDataByID2.IsDisposed)
				{
					if (handleTranslucensePointer)
					{
						objectDataByID2 = objectDataByID2.GetTranslucenceObject();
						if (objectDataByID2 != null && !objectDataByID2.IsDisposed && objectDataByID2 is T)
						{
							list.Add((T)objectDataByID2);
						}
					}
					else if (objectDataByID2 is T)
					{
						list.Add((T)objectDataByID2);
					}
				}
			}
			else
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"ObjectData.GetObjectsFromProperty() - property {propertyID} is not correct PropertyClass");
			}
		}
		return list;
	}

	public void SetObjectToProperty(ObjectData targetObject, ObjectPropertyID propertyID)
	{
		SetObjectsToProperty(new ObjectData[1] { targetObject }, propertyID);
	}

	public void SetObjectsToProperty(IEnumerable<ObjectData> targetObjects, ObjectPropertyID propertyID)
	{
		if (!Properties.Exists(propertyID))
		{
			return;
		}
		ObjectPropertyInstance objectPropertyInstance = Properties.Get(propertyID);
		if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectDataMultiple)
		{
			List<int> list = new List<int>();
			if (targetObjects != null)
			{
				foreach (ObjectData targetObject in targetObjects)
				{
					if (targetObject != null && !targetObject.IsDisposed && !list.Contains(targetObject.ObjectID))
					{
						list.Add(targetObject.ObjectID);
					}
				}
			}
			if (list.Count == 0)
			{
				objectPropertyInstance.Value = "";
			}
			else
			{
				objectPropertyInstance.Value = Converter.IntArrayToString(list.ToArray());
			}
		}
		else if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
		{
			int num = 0;
			if (targetObjects != null)
			{
				foreach (ObjectData targetObject2 in targetObjects)
				{
					if (targetObject2 != null && !targetObject2.IsDisposed)
					{
						num = targetObject2.ObjectID;
						break;
					}
				}
			}
			objectPropertyInstance.Value = num;
		}
		else
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"ObjectTriggerBase.SetTriggerTargetNodes() - property {propertyID} is not correct PropertyClass");
		}
	}

	public void AddObjectToProperty(ObjectData targetObject, ObjectPropertyID propertyID)
	{
		if (targetObject == null || !Properties.Exists(propertyID))
		{
			return;
		}
		ObjectPropertyInstance objectPropertyInstance = Properties.Get(propertyID);
		if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectDataMultiple)
		{
			int[] array = Converter.StringToIntArray((string)objectPropertyInstance.Value);
			int num = 0;
			while (true)
			{
				if (num < array.Length)
				{
					if (array[num] == targetObject.ObjectID)
					{
						break;
					}
					num++;
					continue;
				}
				int[] array2 = new int[array.Length + 1];
				for (int i = 0; i < array2.Length; i++)
				{
					if (i < array.Length)
					{
						array2[i] = array[i];
					}
					else
					{
						array2[i] = targetObject.ObjectID;
					}
				}
				objectPropertyInstance.Value = Converter.IntArrayToString(array2);
				return;
			}
			if (num != array.Length - 1)
			{
				int num2 = array[num];
				array[num] = array[^1];
				array[^1] = num2;
				objectPropertyInstance.Value = Converter.IntArrayToString(array);
			}
		}
		else if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
		{
			objectPropertyInstance.Value = targetObject.ObjectID;
		}
		else
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"ObjectData.AddObjectToProperty() - property {propertyID} is not correct PropertyClass");
		}
	}

	public void RemoveObjectFromProperty(ObjectData targetObject, ObjectPropertyID propertyID)
	{
		if (targetObject == null || !Properties.Exists(propertyID))
		{
			return;
		}
		ObjectPropertyInstance objectPropertyInstance = Properties.Get(propertyID);
		if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectDataMultiple)
		{
			int[] array = Converter.StringToIntArray((string)objectPropertyInstance.Value);
			List<int> list = new List<int>(array.Length);
			for (int i = 0; i < array.Length; i++)
			{
				if (array[i] != targetObject.ObjectID)
				{
					list.Add(array[i]);
				}
			}
			if (list.Count != array.Length)
			{
				objectPropertyInstance.Value = Converter.IntArrayToString(list.ToArray());
			}
		}
		else if (objectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
		{
			if ((int)objectPropertyInstance.Value == targetObject.ObjectID)
			{
				objectPropertyInstance.Value = 0;
			}
		}
		else
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"ObjectData.RemoveObjectFromProperty() - property {propertyID} is not correct PropertyClass");
		}
	}

	public virtual void DrawActivateHightlight(SpriteBatch spriteBatch)
	{
		float num = 1f;
		if (CurrentAnimation != null)
		{
			GetObjectSizeMultiplier(out var x, out var y);
			if (x != 1 || y != 1)
			{
				Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(Body.GetPosition());
				Microsoft.Xna.Framework.Vector2 position = Microsoft.Xna.Framework.Vector2.Zero;
				for (int i = 0; i < x; i++)
				{
					for (int j = 0; j < y; j++)
					{
						position.X = i * CurrentAnimation.FrameWidth;
						position.Y = 0f - (float)(j * CurrentAnimation.FrameHeight);
						SFDMath.RotatePosition(ref position, Body.GetAngle(), out position);
						Microsoft.Xna.Framework.Vector2 vector2 = Camera.ConvertWorldToScreen(vector + position);
						CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(num, num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
						CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(num, 0f - num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
						CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(0f - num, num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
						CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(0f - num, 0f - num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
					}
				}
			}
			else
			{
				Microsoft.Xna.Framework.Vector2 vector2 = Body.GetPosition();
				Camera.ConvertBox2DToScreen(ref vector2, out vector2);
				CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(num, num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
				CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(num, 0f - num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
				CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(0f - num, num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
				CurrentAnimation.Draw(spriteBatch, Texture, vector2 + new Microsoft.Xna.Framework.Vector2(0f - num, 0f - num), Body.GetAngle(), m_faceDirectionSpriteEffect, 1f);
			}
		}
		else
		{
			for (int k = 0; k < m_objectDecals.Count; k++)
			{
				ObjectDecal objectDecal = m_objectDecals[k];
				Microsoft.Xna.Framework.Vector2 vector2 = (objectDecal.HaveOffset ? Body.GetWorldPoint(objectDecal.LocalOffset) : Body.Position);
				Camera.ConvertBox2DToScreen(ref vector2, out vector2);
				spriteBatch.Draw(objectDecal.Texture, vector2 + new Microsoft.Xna.Framework.Vector2(num, num), null, Microsoft.Xna.Framework.Color.White, 0f - Body.GetAngle(), objectDecal.TextureOrigin, Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
				spriteBatch.Draw(objectDecal.Texture, vector2 + new Microsoft.Xna.Framework.Vector2(num, 0f - num), null, Microsoft.Xna.Framework.Color.White, 0f - Body.GetAngle(), objectDecal.TextureOrigin, Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
				spriteBatch.Draw(objectDecal.Texture, vector2 + new Microsoft.Xna.Framework.Vector2(0f - num, num), null, Microsoft.Xna.Framework.Color.White, 0f - Body.GetAngle(), objectDecal.TextureOrigin, Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
				spriteBatch.Draw(objectDecal.Texture, vector2 + new Microsoft.Xna.Framework.Vector2(0f - num, 0f - num), null, Microsoft.Xna.Framework.Color.White, 0f - Body.GetAngle(), objectDecal.TextureOrigin, Camera.ZoomUpscaled, m_faceDirectionSpriteEffect, 0f);
			}
		}
	}

	public virtual FixedArray4<Microsoft.Xna.Framework.Vector2> DrawEditHighlight(World b2_world_active)
	{
		FixedArray4<Microsoft.Xna.Framework.Vector2> result = default(FixedArray4<Microsoft.Xna.Framework.Vector2>);
		b2_world_active.DrawDebugBody(Body);
		FixedArray2<int> textureSize = GetTextureSize();
		ItemContainer<int, int> objectSize = GetObjectSize();
		Microsoft.Xna.Framework.Vector2 position = Converter.ConvertWorldToBox2D(new Microsoft.Xna.Framework.Vector2((float)objectSize.Item1 / 2f, 0f));
		Microsoft.Xna.Framework.Vector2 position2 = Converter.ConvertWorldToBox2D(new Microsoft.Xna.Framework.Vector2(0f, (float)objectSize.Item2 / 2f));
		Microsoft.Xna.Framework.Vector2 zero = Microsoft.Xna.Framework.Vector2.Zero;
		FixedArray2<int> objectSizeMultiplier = GetObjectSizeMultiplier();
		if (objectSizeMultiplier[0] != 1)
		{
			zero.X = position.X * 2f - Converter.ConvertWorldToBox2D(textureSize[0]);
		}
		if (objectSizeMultiplier[1] != 1)
		{
			zero.Y = (0f - position2.Y) * 2f + Converter.ConvertWorldToBox2D(textureSize[1]);
		}
		SFDMath.RotatePosition(ref position, Body.GetAngle(), out position);
		SFDMath.RotatePosition(ref position2, Body.GetAngle(), out position2);
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertWorldToBox2D(Tile.TextureOffset) + zero * 0.5f;
		Microsoft.Xna.Framework.Vector2 vector2 = Body.GetPosition() + vector;
		Microsoft.Xna.Framework.Vector2 position3 = vector;
		SFDMath.RotatePosition(ref position3, Body.GetAngle(), out position3);
		vector2 += position3 - vector;
		b2_world_active.DebugDraw.DrawSegment(vector2 + position + position2, vector2 + position - position2, Microsoft.Xna.Framework.Color.Yellow);
		b2_world_active.DebugDraw.DrawSegment(vector2 - position + position2, vector2 - position - position2, Microsoft.Xna.Framework.Color.Yellow);
		b2_world_active.DebugDraw.DrawSegment(vector2 - position + position2, vector2 + position + position2, Microsoft.Xna.Framework.Color.Yellow);
		b2_world_active.DebugDraw.DrawSegment(vector2 - position - position2, vector2 + position - position2, Microsoft.Xna.Framework.Color.Yellow);
		result[0] = vector2 + position + position2;
		result[1] = vector2 - position + position2;
		result[2] = vector2 + position - position2;
		result[3] = vector2 - position - position2;
		return result;
	}

	public bool DefaultEditCheckTouch(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		Body body = Body;
		if (body == null)
		{
			return false;
		}
		if (Tile.Sizeable == Tile.SIZEABLE.N)
		{
			FixedArray2<int> textureSize = GetTextureSize();
			float num = textureSize[0];
			float num2 = textureSize[1];
			Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(body.GetPosition());
			Microsoft.Xna.Framework.Vector2 position = worldPosition - vector;
			float num3 = ((m_faceDirection == -1) ? (-1f) : 1f);
			position.X *= num3;
			SFDMath.RotatePosition(ref position, (0f - body.GetAngle()) * num3, out position);
			position.X += num / 2f - Tile.TextureOffset.X;
			position.Y += num2 / 2f - Tile.TextureOffset.Y;
			position.X = (float)Math.Round(position.X, 0, MidpointRounding.ToEven);
			position.Y = (float)Math.Round(position.Y, 0, MidpointRounding.ToEven);
			position.Y = num2 - position.Y;
			if (!(position.X < 0f) && !(position.Y < 0f) && !(position.X >= num) && position.Y < num2)
			{
				int num4 = (int)Math.Round(position.X, 0, MidpointRounding.ToEven);
				int num5 = (int)Math.Round(position.Y, 0, MidpointRounding.ToEven);
				if (num4 >= 0 && num5 >= 0 && !((float)num4 >= num) && (float)num5 < num2)
				{
					Microsoft.Xna.Framework.Color[] array = new Microsoft.Xna.Framework.Color[Tile.Texture.Width * Tile.Texture.Height];
					Tile.Texture.GetData(array);
					int num6 = Tile.Texture.Width * num5 + num4;
					if (num6 >= 0 && num6 < array.Length)
					{
						if (array[num6].A > 0)
						{
							return true;
						}
						return false;
					}
					return false;
				}
				return false;
			}
			return false;
		}
		int num7 = 1;
		int num8 = 1;
		FixedArray2<int> textureSize2 = GetTextureSize();
		ItemContainer<int, int> objectSize = GetObjectSize();
		num7 = objectSize.Item1;
		num8 = objectSize.Item2;
		Microsoft.Xna.Framework.Vector2 vector2 = Converter.ConvertBox2DToWorld(body.GetPosition());
		Microsoft.Xna.Framework.Vector2 position2 = worldPosition - vector2;
		SFDMath.RotatePosition(ref position2, 0f - body.GetAngle(), out position2);
		position2.X += textureSize2[0] / 2;
		position2.Y += (float)num8 - (float)(textureSize2[1] / 2);
		position2.X = (float)Math.Round(position2.X, 0, MidpointRounding.ToEven);
		position2.Y = (float)Math.Round(position2.Y, 0, MidpointRounding.ToEven);
		position2.Y = (float)num8 - position2.Y;
		if (!(position2.X < 0f) && !(position2.Y < 0f) && !(position2.X >= (float)num7) && position2.Y < (float)num8)
		{
			return true;
		}
		return false;
	}

	public bool DefaultEditCheckTouch(GameWorld.SelectionArea area)
	{
		if (Body == null)
		{
			return false;
		}
		FixedArray2<int> textureSize = GetTextureSize();
		int num = 1;
		int num2 = 1;
		if (Tile.Sizeable != Tile.SIZEABLE.N)
		{
			if (Properties.Exists(ObjectPropertyID.Size_X))
			{
				num = (int)Properties.Get(ObjectPropertyID.Size_X).Value;
			}
			if (Properties.Exists(ObjectPropertyID.Size_Y))
			{
				num2 = (int)Properties.Get(ObjectPropertyID.Size_Y).Value;
			}
			num *= textureSize[0];
			num2 *= textureSize[1];
		}
		else
		{
			num = (int)((float)textureSize[0] / 2f);
			num2 = (int)((float)textureSize[1] / 2f);
		}
		float y = area.GetTopRight().Y;
		float y2 = area.GetBottomLeft().Y;
		float x = area.GetTopRight().X;
		float x2 = area.GetBottomLeft().X;
		Microsoft.Xna.Framework.Vector2 worldPosition = GetWorldPosition();
		float angle = Body.GetAngle();
		float num3;
		float num4;
		float num5;
		float num6;
		if (angle == 0f)
		{
			num3 = worldPosition.Y + (float)textureSize[1] / 2f;
			num4 = worldPosition.Y - (float)num2;
			num5 = worldPosition.X - (float)textureSize[0] / 2f;
			num6 = worldPosition.X + (float)num;
			if (num6 < x2)
			{
				return false;
			}
			if (num5 > x)
			{
				return false;
			}
			if (num3 < y2)
			{
				return false;
			}
			if (num4 > y)
			{
				return false;
			}
			return true;
		}
		num3 = 0f - (float)textureSize[1] / 2f;
		num4 = (float)num2 - (float)textureSize[1] / 2f;
		num5 = 0f - (float)textureSize[0] / 2f;
		num6 = (float)num - (float)textureSize[0] / 2f;
		Microsoft.Xna.Framework.Vector2 vector = new Microsoft.Xna.Framework.Vector2(num4, num6);
		Microsoft.Xna.Framework.Vector2 vector2 = new Microsoft.Xna.Framework.Vector2(num3, num5);
		FixedArray4<GameWorld.SelectionLine> fixedArray = new FixedArray4<GameWorld.SelectionLine>
		{
			[0] = new GameWorld.SelectionLine(new Microsoft.Xna.Framework.Vector2(vector2.X, vector2.Y), new Microsoft.Xna.Framework.Vector2(vector.X, vector2.Y)),
			[1] = new GameWorld.SelectionLine(new Microsoft.Xna.Framework.Vector2(vector.X, vector2.Y), new Microsoft.Xna.Framework.Vector2(vector.X, vector.Y)),
			[2] = new GameWorld.SelectionLine(new Microsoft.Xna.Framework.Vector2(vector.X, vector.Y), new Microsoft.Xna.Framework.Vector2(vector2.X, vector.Y)),
			[3] = new GameWorld.SelectionLine(new Microsoft.Xna.Framework.Vector2(vector2.X, vector.Y), new Microsoft.Xna.Framework.Vector2(vector2.X, vector2.Y))
		};
		for (int i = 0; i < 4; i++)
		{
			Microsoft.Xna.Framework.Vector2 position = fixedArray[i].Start;
			Microsoft.Xna.Framework.Vector2 position2 = fixedArray[i].End;
			SFDMath.RotatePosition(ref position, angle - (float)Math.PI / 2f, out position);
			SFDMath.RotatePosition(ref position2, angle - (float)Math.PI / 2f, out position2);
			fixedArray[i] = new GameWorld.SelectionLine(position + worldPosition, position2 + worldPosition);
		}
		FixedArray4<GameWorld.SelectionLine> selectionLines = area.GetSelectionLines();
		bool flag;
		if (!(flag = area.Contains(fixedArray[0].Start) || area.Contains(fixedArray[1].Start) || area.Contains(fixedArray[2].Start) || area.Contains(fixedArray[3].Start)))
		{
			for (int j = 0; j < 4; j++)
			{
				for (int k = 0; k < 4; k++)
				{
					if (SFDMath.CheckLineLineIntersection(selectionLines[j].Start, selectionLines[j].End, fixedArray[k].Start, fixedArray[k].End))
					{
						flag = true;
						j = 4;
						k = 4;
					}
				}
			}
			if (!flag)
			{
				flag = DefaultEditCheckTouch(selectionLines[0].Start) || DefaultEditCheckTouch(selectionLines[1].Start) || DefaultEditCheckTouch(selectionLines[2].Start) || DefaultEditCheckTouch(selectionLines[3].Start);
			}
		}
		return flag;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ObjectData Read(Fixture fixture)
	{
		return (ObjectData)fixture.GetUserData();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ObjectData Read(Body body)
	{
		return BodyData.Read(body).Object;
	}

	public override string ToString()
	{
		return "ObjectData Id=" + ObjectID + " CustomId=" + CustomID + " MapObjectId=" + MapObjectID + " " + ((!IsPlayer || InternalData == null) ? "" : ("(" + ((Player)InternalData).ToString() + ")"));
	}

	public static void Save(ObjectData od, SFDBinaryWriter writer)
	{
		writer.Write(0);
		writer.Write(od.MapObjectID);
		writer.Write(1);
		writer.Write(od.CustomID);
		writer.Write(10);
		writer.Write(od.GroupID);
		writer.Write(2);
		writer.Write(od.GetWorldPosition());
		writer.Write(3);
		writer.Write(od.GetAngle());
		writer.Write(4);
		writer.Write(od.LocalRenderLayer);
		writer.Write(5);
		writer.Write((int)od.FaceDirection);
		writer.Write(6);
		writer.Write(od.Colors.Length);
		for (int i = 0; i < od.Colors.Length; i++)
		{
			writer.Write((od.Colors[i] != null) ? od.Colors[i] : "");
		}
		writer.Write(7);
		ObjectProperties.Save(od.Properties, writer);
		writer.Write(9999);
	}

	public static void Load(GameWorld gameWorld, SFDBinaryReader reader)
	{
		Dictionary<ObjectDataValue, object> dictionary = new Dictionary<ObjectDataValue, object>();
		string text = "";
		int num = 0;
		if (reader.BaseStream.Position <= reader.BaseStream.Length - 4L)
		{
			bool flag = false;
			ObjectDataValue objectDataValue = (ObjectDataValue)reader.ReadInt32();
			while (objectDataValue != ObjectDataValue.END)
			{
				object obj = null;
				switch (objectDataValue)
				{
				case ObjectDataValue.MapObjectID:
					if (flag)
					{
						objectDataValue = ObjectDataValue.END;
						reader.BaseStream.Position -= 4L;
						gameWorld.EditMapFileIsCorrupt = true;
					}
					else
					{
						flag = true;
						text = TileDatabase.UpdateTileKey(reader.ReadString());
						obj = text;
					}
					break;
				case ObjectDataValue.CustomID:
					num = reader.ReadInt32();
					obj = num;
					break;
				case ObjectDataValue.WorldPosition:
					obj = reader.ReadVector2();
					break;
				case ObjectDataValue.Angle:
					obj = reader.ReadSingle();
					break;
				case ObjectDataValue.LocalRenderLayer:
					obj = reader.ReadInt32();
					break;
				case ObjectDataValue.FaceDirection:
					obj = reader.ReadInt32();
					break;
				case ObjectDataValue.Colors:
				{
					int num2 = reader.ReadInt32();
					string[] array = new string[num2];
					for (int i = 0; i < num2; i++)
					{
						array[i] = reader.ReadString();
					}
					if (3 != array.Length)
					{
						string[] array2 = new string[3];
						for (int j = 0; j < array2.Length; j++)
						{
							array2[j] = ((j < array.Length) ? array[j] : "");
						}
						array = array2;
					}
					obj = array;
					break;
				}
				case ObjectDataValue.Properties:
					obj = ObjectProperties.Load(reader, networkSilent: true);
					break;
				case ObjectDataValue.GroupID:
					obj = reader.ReadUInt16();
					break;
				}
				if (objectDataValue != ObjectDataValue.END)
				{
					if (obj != null && !dictionary.ContainsKey(objectDataValue))
					{
						dictionary.Add(objectDataValue, obj);
					}
					if (reader.BaseStream.Position <= reader.BaseStream.Length - 4L)
					{
						objectDataValue = (ObjectDataValue)reader.ReadInt32();
						continue;
					}
					objectDataValue = ObjectDataValue.END;
					gameWorld.EditMapFileIsCorrupt = true;
				}
			}
		}
		else
		{
			gameWorld.EditMapFileIsCorrupt = true;
		}
		if (!TileDatabase.Exist(text))
		{
			text = "error";
		}
		ObjectData objectData = gameWorld.IDCounter.NextObjectData(text, num);
		List<ObjectProperties.ObjectPropertyValueInstance> list = null;
		ushort groupID = 0;
		Microsoft.Xna.Framework.Vector2 worldPosition = Microsoft.Xna.Framework.Vector2.Zero;
		float rotation = 0f;
		short faceDirection = 1;
		Microsoft.Xna.Framework.Vector2 linearVelocity = Microsoft.Xna.Framework.Vector2.Zero;
		float angularVelocity = 0f;
		int layer = 0;
		string[] colors = null;
		if (dictionary.ContainsKey(ObjectDataValue.GroupID))
		{
			groupID = (ushort)dictionary[ObjectDataValue.GroupID];
		}
		if (dictionary.ContainsKey(ObjectDataValue.WorldPosition))
		{
			worldPosition = (Microsoft.Xna.Framework.Vector2)dictionary[ObjectDataValue.WorldPosition];
		}
		if (dictionary.ContainsKey(ObjectDataValue.Angle))
		{
			rotation = (float)dictionary[ObjectDataValue.Angle];
		}
		if (dictionary.ContainsKey(ObjectDataValue.FaceDirection))
		{
			faceDirection = (short)(int)dictionary[ObjectDataValue.FaceDirection];
		}
		if (dictionary.ContainsKey(ObjectDataValue.LinearVelocity))
		{
			linearVelocity = (Microsoft.Xna.Framework.Vector2)dictionary[ObjectDataValue.LinearVelocity];
		}
		if (dictionary.ContainsKey(ObjectDataValue.AngularVelocity))
		{
			angularVelocity = (float)dictionary[ObjectDataValue.AngularVelocity];
		}
		if (dictionary.ContainsKey(ObjectDataValue.LocalRenderLayer))
		{
			layer = (int)dictionary[ObjectDataValue.LocalRenderLayer];
		}
		if (dictionary.ContainsKey(ObjectDataValue.Colors))
		{
			colors = (string[])dictionary[ObjectDataValue.Colors];
		}
		if (dictionary.ContainsKey(ObjectDataValue.Properties))
		{
			list = (List<ObjectProperties.ObjectPropertyValueInstance>)dictionary[ObjectDataValue.Properties];
			if (list != null && list.Count > 0)
			{
				ObjectProperties.ObjectPropertyValueInstance objectPropertyValueInstance = list.Find((ObjectProperties.ObjectPropertyValueInstance x) => x.PropertyID == 98);
				if (objectPropertyValueInstance != null)
				{
					ObjectProperties.ObjectPropertyValueInstance objectPropertyValueInstance2 = new ObjectProperties.ObjectPropertyValueInstance();
					objectPropertyValueInstance2.NetworkSilent = true;
					objectPropertyValueInstance2.PropertyID = 290;
					objectPropertyValueInstance2.ValueType = 0;
					objectPropertyValueInstance2.Value = Converter.IntArrayToString(new int[1] { (int)objectPropertyValueInstance.Value });
					list.Add(objectPropertyValueInstance2);
					list.Remove(objectPropertyValueInstance);
				}
			}
		}
		SpawnObjectInformation spawnObjectInformation = new SpawnObjectInformation(objectData, worldPosition, rotation, faceDirection, layer, linearVelocity, angularVelocity);
		spawnObjectInformation.Colors = colors;
		spawnObjectInformation.PropertyValues = list;
		spawnObjectInformation.GroupID = groupID;
		try
		{
			if (spawnObjectInformation.GroupID > 0)
			{
				gameWorld.AddSpawnInfoToGroup(spawnObjectInformation);
			}
			else
			{
				gameWorld.CreateTile(spawnObjectInformation);
			}
		}
		catch (ThreadAbortException)
		{
			throw;
		}
		catch (GameWorldCorruptedException)
		{
			throw;
		}
		catch
		{
		}
	}

	public void ScriptMethod(ObjectDataSyncedMethod.Methods method)
	{
		ScriptMethod(method, null);
	}

	public void ScriptMethod(ObjectDataSyncedMethod.Methods method, params object[] args)
	{
		ObjectDataSyncedMethod syncMethod = new ObjectDataSyncedMethod(method, GameWorld.ElapsedTotalGameTime, args);
		SyncedMethod(syncMethod);
	}

	public uint ScriptMethodNextCount(ObjectDataSyncedMethod.Methods method)
	{
		uint num = ScriptMethodCurrentCount(method);
		num++;
		ScriptMethodStoreCount(method, num);
		return num;
	}

	public uint ScriptMethodCurrentCount(ObjectDataSyncedMethod.Methods method)
	{
		if (m_syncedMethodCounter == null)
		{
			return 0u;
		}
		uint value = 0u;
		if (!m_syncedMethodCounter.TryGetValue(method, out value))
		{
			return 0u;
		}
		return value;
	}

	public uint ScriptMethodStoreCount(ObjectDataSyncedMethod.Methods method, uint methodCount)
	{
		if (m_syncedMethodCounter == null)
		{
			m_syncedMethodCounter = new Dictionary<ObjectDataSyncedMethod.Methods, uint>();
		}
		m_syncedMethodCounter[method] = methodCount;
		return methodCount;
	}

	public void SyncedMethod(ObjectDataSyncedMethod syncMethod)
	{
		if (syncMethod.MethodCount == 0)
		{
			if (GameOwner == GameOwnerEnum.Client)
			{
				syncMethod.MethodCount = ScriptMethodCurrentCount(syncMethod.Method);
			}
			else
			{
				syncMethod.MethodCount = ScriptMethodNextCount(syncMethod.Method);
			}
		}
		if (GameOwner == GameOwnerEnum.Server && GameSFD.Handle.Server != null && GameSFD.Handle.Server.NetServer != null)
		{
			NetOutgoingMessage netOutgoingMessage = GameSFD.Handle.Server.NetServer.CreateMessage();
			NetMessage.ScriptMethodData.Write(syncMethod, ObjectID, netOutgoingMessage);
			GameSFD.Handle.Server.NetServer.SendToAll(netOutgoingMessage, null, NetMessage.ScriptMethodData.Delivery.Method, NetMessage.ScriptMethodData.Delivery.Channel);
			NetPeerPool.Instance.Recycle(netOutgoingMessage);
		}
		uint num = ScriptMethodCurrentCount(syncMethod.Method);
		if (syncMethod.MethodCount < num)
		{
			return;
		}
		ScriptMethodStoreCount(syncMethod.Method, syncMethod.MethodCount);
		switch (syncMethod.Method)
		{
		default:
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Unhandled ScriptMethod: " + syncMethod.Method);
			break;
		case ObjectDataSyncedMethod.Methods.SetWeaponItemProperties:
		{
			bool num2 = (bool)syncMethod.Args[0];
			bool flag2 = (bool)syncMethod.Args[1];
			bool awake = (bool)syncMethod.Args[2];
			Activateable = flag2;
			ActivateableHighlightning = flag2;
			Body.SetAwake(awake);
			string text = Tile.TextureName;
			if (num2 && Textures.TryGetTexture(text + "H", out var _))
			{
				text += "H";
			}
			ClearDecals();
			AddDecal(new ObjectDecal(Textures.GetTexture(text)));
			break;
		}
		case ObjectDataSyncedMethod.Methods.AnimationSetFrame:
		{
			int frame = (int)syncMethod.Args[0];
			bool flag = (bool)syncMethod.Args[1];
			if (CurrentAnimation != null)
			{
				if (CurrentAnimation.IsSynced)
				{
					UnsyncSyncedAnimation();
				}
				CurrentAnimation.SetFrame(frame);
				if (flag)
				{
					CurrentAnimation.Pause();
				}
				else
				{
					CurrentAnimation.Play();
				}
				if (!CurrentAnimation.IsPaused)
				{
					float elapsedLocalGameTimeSinceTimestamp = GameWorld.GetElapsedLocalGameTimeSinceTimestamp(syncMethod.Timestamp);
					CurrentAnimation.Progress(elapsedLocalGameTimeSinceTimestamp);
				}
			}
			break;
		}
		}
	}

	public static ObjectData CreateNew(ObjectDataStartParams startParams)
	{
		switch (startParams.MapObjectID)
		{
		case "ITEMBOUNCINGAMMO":
			return new ObjectWeaponItem(startParams, 66);
		case "ITEMMEDKIT":
			return new ObjectWeaponItem(startParams, 14);
		case "LAMP00_D":
			return new ObjectElectric(startParams);
		case "ATLASSTATUE00":
			return new ObjectDestructible(startParams, "AtlasStatue00_D", "AtlasStatue00Globe", "StoneDebris00A", "StoneDebris00B", "StoneDebris00C");
		case "WPNPISTOL45":
			return new ObjectWeaponItem(startParams, 61);
		case "SPAWNWEAPONAREACEILING":
			return new ObjectTransparentEditor(startParams);
		case "DESTROYTARGETS":
			return new ObjectDestroyTargets(startParams);
		case "WOODAWNING00B":
			return new ObjectDestructible(startParams, "WoodAwning00B_D", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C");
		case "SPAWNITEMTRIGGER":
			return new ObjectSpawnItemTrigger(startParams);
		case "ITEMSLOMO5":
			return new ObjectWeaponItem(startParams, 15);
		case "ITEMSPEEDBOOST":
			return new ObjectWeaponItem(startParams, 63);
		case "STREETSWEEPERCRATE":
			return new ObjectStreetsweeperCrate(startParams);
		case "WPNSMG":
			return new ObjectWeaponItem(startParams, 30);
		case "WPNSNIPERRIFLE":
			return new ObjectWeaponItem(startParams, 9);
		case "LIGHTSIGN01O":
			return new ObjectElectric(startParams, "LightSign01O_D");
		case "ALTERNATINGTRIGGER":
			return new ObjectAlternatingTrigger(startParams);
		case "LIGHTSIGN01N_D":
			return new ObjectElectric(startParams);
		case "WPNSHURIKENTHROWN":
			return new ObjectShurikenThrown(startParams);
		case "LIGHTSIGN01N":
			return new ObjectElectric(startParams, "LightSign01N_D");
		case "WPNMACHINEPISTOL":
			return new ObjectWeaponItem(startParams, 53);
		case "EMPTY":
			return new ObjectEmpty(startParams);
		case "LIGHTSIGN01I":
			return new ObjectElectric(startParams, "LightSign01I_D");
		case "LEVER01":
			return new ObjectButtonTrigger(startParams);
		case "LEVER00":
			return new ObjectButtonTrigger(startParams);
		case "DISTANCEJOINT":
			return new ObjectDistanceJoint(startParams);
		case "SPAWNOBJECTTRIGGER":
			return new ObjectSpawnObjectTrigger(startParams);
		case "XMASTREE":
			return new ObjectDestructible(startParams, "XMASTREE_D")
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "LIGHTSIGN01K":
			return new ObjectElectric(startParams, "LightSign01K_D");
		case "CUESTICK00":
			return new ObjectWeaponItem(startParams, 36, "CueStick00Shaft");
		case "WPNUZI":
			return new ObjectWeaponItem(startParams, 12);
		case "FARBGCLOUDS00C":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.01f, 0f));
		case "WPNCARBINE":
			return new ObjectWeaponItem(startParams, 23);
		case "BALLOON00":
			return new ObjectBalloon(startParams)
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "ACIDZONE":
			return new ObjectSplashZone(startParams, "ACS");
		case "TINROOF00_D":
			return new ObjectDestructible(startParams, "", "MetalDebris00A", "MetalDebris00B");
		case "BASEBALL":
			return new ObjectWeaponItem(startParams, 58);
		case "WPNFLAMETHROWER":
			return new ObjectWeaponItem(startParams, 26);
		case "WPNCHAINSAW":
			return new ObjectWeaponItem(startParams, 59);
		case "BARRELWRECK":
			return new ObjectBarrelWreck(startParams);
		case "REINFORCEDGLASS00A":
			return new ObjectReinforcedGlass(startParams, "", "GlassShard00A");
		case "SPAWNTARGET":
			return new ObjectSpawnTarget(startParams);
		case "SUITCASE00":
			return new ObjectWeaponItem(startParams, 38);
		case "ELEVATORATTACHMENTJOINT":
			return new ObjectElevatorAttachmentJoint(startParams);
		case "GAMEOVERTRIGGER":
			return new ObjectGameOverTrigger(startParams);
		case "WPNREVOLVER":
			return new ObjectWeaponItem(startParams, 28);
		case "WPNSILENCEDUZI":
			return new ObjectWeaponItem(startParams, 40);
		case "ELEVATORATTACHMENTJOINTVALUETRIGGER":
			return new ObjectElevatorAttachmentJointValueTrigger(startParams);
		case "RALLYPOINTTRIGGER":
			return new ObjectRallyPointTrigger(startParams);
		case "XMASPRESENT00":
			return new ObjectXmasPresent(startParams);
		case "CARNIVALCART01":
			return new ObjectDestructible(startParams, "CarnivalCart01_D", "MetalDebris00A", "MetalDebris00B", "MetalDebris00C");
		case "DEADMEAT00":
			return new ObjectDestructible(startParams, "DeadMeat00_D", "Giblet01");
		case "FARBGSKYLINESEARCHLIGHT00":
		case "FARBGSEARCHLIGHT01":
		case "FARBGSEARCHLIGHT00":
			return new ObjectFarBgSearchlight(startParams);
		case "PATHZONE":
			return new ObjectPathZone(startParams);
		case "WPNC4THROWN":
			return new ObjectC4Thrown(startParams);
		case "SCRIPTTRIGGER":
			return new ObjectScriptTrigger(startParams);
		case "LIGHTSIGN00T_D":
			return new ObjectElectric(startParams, "", "LightSign00Debris1", "LightSign00Debris1", "LightSign00Debris1");
		case "WPNSAWEDOFF":
			return new ObjectWeaponItem(startParams, 10);
		case "PIANO00_D":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C", "WoodDebris00D", "WoodDebris00E")
			{
				HasGlobalFootstepSound = true
			};
		case "SAWBLADE00":
			return new ObjectSawblade(startParams);
		case "PLAYERCOMMANDCLEARTRIGGER":
			return new ObjectPlayerCommandClearTrigger(startParams);
		case "WPNMINETHROWN":
			return new ObjectMineThrown(startParams);
		case "SPAWNWEAPON":
			return new ObjectSpawnWeapon(startParams);
		case "WPNMOLOTOVS":
			return new ObjectWeaponItem(startParams, 25);
		case "ONPLAYERDEATHTRIGGER":
			return new ObjectOnPlayerDeathTrigger(startParams);
		case "PADLOCK00":
			return new ObjectDestructible(startParams, "", "Padlock00_D")
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreFeetPerformArm
			};
		case "TIMERTRIGGER":
			return new ObjectTimerTrigger(startParams);
		case "PLAYERCOMMANDTRIGGER":
			return new ObjectPlayerCommandTrigger(startParams);
		case "WINDMILLSAIL00":
			return new ObjectDestructible(startParams, "WindMillSail00_D");
		case "PILLOW00":
			return new ObjectWeaponItem(startParams, 47);
		case "SSSFLAG00A_D":
			return new ObjectDefault(startParams)
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "FLAGPOLEUS":
			return new ObjectWeaponItem(startParams, 48);
		case "BGNEON00B":
		case "BGNEON00C":
		case "BGNEON00F":
		case "BGNEON00A":
		case "BGNEON00D":
		case "BGNEON00G":
		case "BGNEON00E":
		case "BGNEON01E":
		case "BGNEON01G":
		case "BGNEON01D":
		case "BGNEON01A":
		case "BGNEON01F":
		case "BGNEON01B":
		case "BGNEON01C":
			return new ObjectNeon(startParams);
		case "AMMOSTASH00":
			return new ObjectAmmoStashTrigger(startParams);
		case "CRANE00A":
			return new ObjectDestructible(startParams, "Crane00A_D");
		case "WINDMILLSAIL00_D":
			return new ObjectDestructible(startParams, "WindMillSail00_D2");
		case "PATHNODE":
			return new ObjectPathNode(startParams);
		case "MEDICALCABINET00":
			return new ObjectMedicalCabinetTrigger(startParams);
		case "ALTERCOLLISIONTILE":
			return new ObjectAlterCollisionTile(startParams);
		case "WPNBOW":
			return new ObjectWeaponItem(startParams, 64);
		case "DUCT00C":
			return new ObjectDestructible(startParams, "Duct00C_D");
		case "WPNCHAIN":
			return new ObjectWeaponItem(startParams, 46);
		case "ELEVATORPATHJOINT":
			return new ObjectElevatorPathJoint(startParams);
		case "WPNGRENADELAUNCHER":
			return new ObjectWeaponItem(startParams, 29);
		case "CRABCAN00":
			return new ObjectDestructible(startParams, "CrabCan00_D");
		case "GIBZONECLEAN":
			return new ObjectGibZone(startParams, cleanGib: true);
		case "TRASHBAG00":
			return new ObjectWeaponItem(startParams, 52);
		case "PLAYERSPAWNAREA":
			return new ObjectPlayerSpawnArea(startParams);
		case "LIGHTSIGN01K_D":
			return new ObjectElectric(startParams);
		case "RAILJOINT":
			return new ObjectRailJoint(startParams);
		case "TABLE00":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebrisTable00A", "WoodDebrisTable00B")
			{
				BotAIForceRegisterCoverCollision = true
			};
		case "HELICOPTER00":
			return new ObjectHelicopter(startParams, "Helicopter00_D");
		case "WPNPUMPSHOTGUN":
			return new ObjectWeaponItem(startParams, 2);
		case "WPNHAMMER":
			return new ObjectWeaponItem(startParams, 31);
		case "PATHNODECONNECTION":
			return new ObjectPathNodeConnection(startParams);
		case "REMOVEAREATRIGGER":
			return new ObjectRemoveAreaTrigger(startParams);
		case "PLAYERSPAWNTRIGGER":
			return new ObjectPlayerSpawnTrigger(startParams);
		case "WPNBAT":
			return new ObjectWeaponItem(startParams, 11);
		case "WPNGRENADESTHROWN":
			return new ObjectGrenadeThrown(startParams);
		case "STREETLAMP00":
			return new ObjectElectric(startParams, "StreetLamp00_D");
		case "REVOLUTEJOINTVALUETRIGGER":
			return new ObjectRevoluteJointValueTrigger(startParams);
		case "WATERZONE":
			return new ObjectSplashZone(startParams, "WS");
		case "BALLOON00_D":
			return new ObjectBalloonD(startParams)
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "BARRELEXPLOSIVE":
			return new ObjectBarrelExplosive(startParams);
		case "SURVEILLANCECAMERA00A":
			return new ObjectElectric(startParams);
		case "FARBGCLOUDS02A":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.02f, 0f));
		case "LIGHTBULB00":
			return new ObjectElectric(startParams);
		case "ITEMSTRENGTHBOOST":
			return new ObjectWeaponItem(startParams, 62);
		case "SPOTLIGHT00A_D":
			return new ObjectBarrelWreck(startParams);
		case "WPNBATON":
			return new ObjectWeaponItem(startParams, 41);
		case "FARBGCLOUDS02C":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.025f, 0f));
		case "DOONCETRIGGER":
			return new ObjectDoOnceTrigger(startParams);
		case "FARBGCLOUDS02B":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.025f, 0f));
		case "CHANGEBODYTYPETRIGGER":
			return new ObjectChangeBodyTypeTrigger(startParams);
		case "BGGAUGE00E":
			return new ObjectGauge(startParams, "BgGaugeHand00", new Microsoft.Xna.Framework.Vector2(0.5f, 0.5f), 0.03f);
		case "LEDGEGRAB":
			return new ObjectLedgeGrab(startParams);
		case "BGGAUGE00D":
			return new ObjectGauge(startParams, "BgGaugeHand01", new Microsoft.Xna.Framework.Vector2(-0.5f, -0.5f), 0.02f);
		case "BULLETPROOFGLASS00WEAK":
			return new ObjectDestructible(startParams, "", "GlassShard00A", "GlassShard00A", "GlassShard00A");
		case "LAMP00":
			return new ObjectElectric(startParams, "LightShaft02A", new Microsoft.Xna.Framework.Vector2(32f, -4f), "Lamp00_D");
		case "LAMP01":
			return new ObjectElectric(startParams, "LightShaft01A", new Microsoft.Xna.Framework.Vector2(24f, -2f), "Lamp01_D");
		case "BGGAUGE00F":
			return new ObjectGauge(startParams, "BgGaugeHand00", new Microsoft.Xna.Framework.Vector2(0.5f, 0.5f), 0.03f);
		case "GIBLET04":
		case "GIBLET02":
		case "GIBLET03":
		case "GIBLET00":
		case "GIBLET01":
			return new ObjectGiblet(startParams);
		case "LIGHTSIGN00T":
			return new ObjectElectric(startParams, "LightSign00T_D");
		case "WOODDOOR00":
			return new ObjectDoor(startParams);
		case "BGGAUGE00A":
			return new ObjectGauge(startParams, "BgGaugeHand00", new Microsoft.Xna.Framework.Vector2(-1.5f, 0.5f), 0.03f);
		case "CHECKALIVETRIGGER":
			return new ObjectCheckAliveTrigger(startParams);
		case "PLAYERMODIFIERINFO":
			return new ObjectPlayerModifierInfo(startParams);
		case "LIGHTSIGN00H":
			return new ObjectElectric(startParams, "LightSign00H_D");
		case "BGGAUGE00C":
			return new ObjectGauge(startParams, "BgGaugeHand00", new Microsoft.Xna.Framework.Vector2(-0.5f, 0.5f), 0.03f);
		case "BGGAUGE00B":
			return new ObjectGauge(startParams, "BgGaugeHand00", new Microsoft.Xna.Framework.Vector2(0.5f, 0.5f), 0.03f);
		case "SPAWNWEAPONAREA":
			return new ObjectSpawnWeaponArea(startParams);
		case "FILECAB00":
			return new ObjectDestructible(startParams, "PaperStack00", "MetalDebris00A", "MetalDebris00B", "MetalDebris00C");
		case "LIGHTSIGN00O":
			return new ObjectElectric(startParams, "LightSign00O_D");
		case "DESTROYTRIGGER":
			return new ObjectDestroyTrigger(startParams);
		case "SETSTICKYFEETTRIGGER":
			return new ObjectSetStickyFeetTrigger(startParams);
		case "ITEMSLOMO10":
			return new ObjectWeaponItem(startParams, 16);
		case "LIGHTSIGN00L":
			return new ObjectElectric(startParams, "LightSign00L_D");
		case "JUKEBOX00":
			return new ObjectButtonTrigger(startParams, "CoinSlot");
		case "FARBGCLOUDS01B":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.03f, 0f));
		case "BGGAUGE00H":
			return new ObjectGauge(startParams, "BgGaugeHand02", new Microsoft.Xna.Framework.Vector2(-0.5f, 0.5f), 0.02f);
		case "FARBGCLOUDS01D":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.01f, 0f));
		case "FARBGCLOUDS01C":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.02f, 0f));
		case "REVOLUTEJOINT":
			return new ObjectRevoluteJoint(startParams);
		case "GLASSSHARD00A":
			return new ObjectDestructible(startParams, "", "");
		case "CONVERTTILETYPE":
			return new ObjectConvertTileType(startParams);
		case "SPOTLIGHT00AWEAK":
			return new ObjectElectric(startParams, "LightShaftSpotlight", new Microsoft.Xna.Framework.Vector2(4f, -6f), "Spotlight00A_D");
		case "GASCAN00":
		case "PROPANETANK":
			return new ObjectExplosive(startParams)
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreFeetPerformArm
			};
		case "WOODSUPPORT00A":
		case "WOODSUPPORT00B":
			return new ObjectWoodSupport(startParams);
		case "CONVEYORBELT00A":
			return new ObjectConveyorBelt(startParams, ObjectConveyorBelt.ConveyorBeltType.Start);
		case "WPNASSAULTRIFLE":
			return new ObjectWeaponItem(startParams, 19);
		case "LIGHTSIGN00E":
			return new ObjectElectric(startParams, "LightSign00E_D");
		case "WPNPISTOL":
			return new ObjectWeaponItem(startParams, 24);
		case "RANDOMTRIGGER":
			return new ObjectRandomTrigger(startParams);
		case "SETMAPPARTTRIGGER":
			return new ObjectSetMapPartTrigger(startParams);
		case "CONVEYORBELT00C":
			return new ObjectConveyorBelt(startParams, ObjectConveyorBelt.ConveyorBeltType.End);
		case "ITEMPILLS":
			return new ObjectWeaponItem(startParams, 13);
		case "CONVEYORBELT00B":
			return new ObjectConveyorBelt(startParams, ObjectConveyorBelt.ConveyorBeltType.Mid);
		case "ITEMFIREAMMO":
			return new ObjectWeaponItem(startParams, 67);
		case "GIBZONE":
			return new ObjectGibZone(startParams);
		case "WOODBARREL00":
		case "WOODBARREL01":
			return new ObjectDestructible(startParams, "", "WoodBarrelDebris00A", "WoodBarrelDebris00B", "WoodBarrelDebris00A", "WoodBarrelDebris00C");
		case "DOVE00":
			return new ObjectBird(startParams);
		case "DIALOGUETRIGGER":
			return new ObjectDialogueTrigger(startParams);
		case "LIGHTSIGN00E_D":
			return new ObjectElectric(startParams, "", "LightSign00Debris1", "LightSign00Debris1", "LightSign00Debris1");
		case "RAILATTACHMENTJOINTVALUETRIGGER":
			return new ObjectRailAttachmentJointValueTrigger(startParams);
		case "TELEPORTTRIGGER":
			return new ObjectTeleportTrigger(startParams);
		case "PLAYSOUNDTRIGGER":
			return new ObjectPlaySoundTrigger(startParams);
		case "ONGAMEOVERTRIGGER":
			return new ObjectOnGameOverTrigger(startParams);
		case "PLAYERTERMINATETRIGGER":
			return new ObjectPlayerTerminateTrigger(startParams);
		case "FACEATOBJECT":
			return new ObjectFaceAtObject(startParams);
		case "WPNMAGNUM":
			return new ObjectWeaponItem(startParams, 1);
		case "WPNMINES":
			return new ObjectWeaponItem(startParams, 44);
		case "STREETSWEEPERWRECK":
			return new ObjectStreetsweeperWreck(startParams);
		case "EDITORTEXT":
			return new ObjectEditorText(startParams);
		case "BOTTLE00":
			return new ObjectWeaponItem(startParams, 34, "Bottle00Broken");
		case "WOODRAILING00":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebris00B");
		case "LIGHTSIGN00L_D":
			return new ObjectElectric(startParams, "", "LightSign00Debris1", "LightSign00Debris1", "LightSign00Debris1");
		case "PLAYERINPUTENABLETRIGGER":
			return new ObjectPlayerInputEnableTrigger(startParams);
		case "HANGINGCRATE00":
		case "SUPPLYCRATEWEAPON":
		case "HANGINGCRATE01":
		case "SUPPLYCRATEMEDIC":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C", "GasMask00", "Boot00", "Helmet00", "CrabCan00");
		case "SETFRAMETRIGGER":
			return new ObjectSetFrameTrigger(startParams);
		case "SPAWNPLAYER":
			return new ObjectPlayerSpawnMarker(startParams);
		case "CARDBOARDBOX00":
			return new ObjectDestructible(startParams, "CardboardBox00_D")
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "WPNSHOCKBATON":
			return new ObjectWeaponItem(startParams, 57);
		case "PIANO00":
			return new ObjectDestructible(startParams, "Piano00_D")
			{
				HasGlobalFootstepSound = true
			};
		case "BGGAUGES00A":
			return new ObjectGauge(startParams, "BgGaugeHand00", new Microsoft.Xna.Framework.Vector2(-0.5f, 0.5f), 0.0075f);
		case "MONITOR00":
			return new ObjectElectric(startParams, "Monitor00_D");
		case "TILEWALL00A":
			return new ObjectTileWall(startParams);
		case "HANGINGDUCT00":
			return new ObjectDestructible(startParams, "HangingDuct00_D", "MetalDebris00A", "MetalDebris00B", "MetalDebris00C");
		case "TILEWALL00B":
			return new ObjectTileWall(startParams);
		case "TILEWALL00C":
			return new ObjectTileWall(startParams);
		case "WPNSILENCEDPISTOL":
			return new ObjectWeaponItem(startParams, 39);
		case "SEARCHLIGHT00":
			return new ObjectSearchLight(startParams);
		case "ONPLAYERDAMAGETRIGGER":
			return new ObjectOnPlayerDamageTrigger(startParams);
		case "EDITORTESTBUTTON00":
			return new ObjectButtonTrigger(startParams, "ButtonPush1", editorOnlyButton: true);
		case "ITEMLASERSIGHT":
			return new ObjectWeaponItem(startParams, 21);
		case "WPNGRENADES":
			return new ObjectWeaponItem(startParams, 20);
		case "GROUPMARKER":
			return new ObjectGroupMarker(startParams);
		case "WPNKATANA":
			return new ObjectWeaponItem(startParams, 3);
		case "FARBGCLOUD00A":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.05f));
		case "HANGINGLAMP00":
			return new ObjectElectric(startParams, "LightShaft01A", new Microsoft.Xna.Framework.Vector2(24f, -4f), "HangingLamp00_D");
		case "CHAIRLEG":
			return new ObjectWeaponItem(startParams, 33);
		case "SOUNDAREA":
			return new ObjectSoundArea(startParams);
		case "HELICOPTER00_D":
			return new ObjectHelicopter(startParams, "");
		case "PULLEYJOINT":
			return new ObjectPulleyJoint(startParams);
		case "LIGHTSIGN01I_D":
			return new ObjectElectric(startParams);
		case "PORTAL":
		case "PORTALD":
		case "PORTALU":
			return new ObjectPortal(startParams);
		case "TARGETOBJECTJOINT":
			return new ObjectTargetObjectJoint(startParams);
		case "TRASHCAN00LID":
			return new ObjectWeaponItem(startParams, 51);
		case "LIGHTSIGN00H_D":
			return new ObjectElectric(startParams, "", "LightSign00Debris1", "LightSign00Debris1", "LightSign00Debris1");
		case "WPNC4":
			return new ObjectWeaponItem(startParams, 42);
		case "PATHDEBUGTARGET":
			return new ObjectPathDebugTarget(startParams);
		case "WPNAXE":
			return new ObjectWeaponItem(startParams, 18);
		case "WPNMOLOTOVSTHROWN":
			return new ObjectMolotovThrown(startParams);
		case "TINROOF00":
			return new ObjectDestructible(startParams, "TinRoof00_D");
		case "CHECKTEAMALIVETRIGGER":
			return new ObjectCheckTeamAliveTrigger(startParams);
		case "PATHNODEENABLETRIGGER":
			return new ObjectPathNodeEnableTrigger(startParams);
		case "ONDESTROYEDTRIGGER":
			return new ObjectOnDestroyedTrigger(startParams);
		case "RUG00A":
			return new ObjectDestructible(startParams, "Rug00A_D");
		case "WPNC4DETONATOR":
			return new ObjectWeaponItem(startParams, 43);
		case "PLAYERPROFILEINFO":
			return new ObjectPlayerProfileInfo(startParams);
		case "EXPLOSIONTRIGGER":
			return new ObjectExplosionTrigger(startParams);
		case "WPNTOMMYGUN":
			return new ObjectWeaponItem(startParams, 5);
		case "WPNWHIP":
			return new ObjectWeaponItem(startParams, 65);
		case "BUTTON01":
		case "BUTTON00":
			return new ObjectButtonTrigger(startParams);
		case "CRATEEXPLOSIVE00":
			return new ObjectExplosive(startParams, "", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C");
		case "SUPPLYCRATE00":
			return new ObjectSupplyCrate(startParams);
		case "WPNM60":
			return new ObjectWeaponItem(startParams, 6);
		case "CARDBOARDBOX00_D":
			return new ObjectDefault(startParams)
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "BOTTLE00BROKEN":
			return new ObjectWeaponItem(startParams, 35);
		case "BRIDGEPLANK00":
		case "BRIDGEPLANK01":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebris00B");
		case "XMASTREE_D":
			return new ObjectDefault(startParams)
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "MUSICTRIGGER":
			return new ObjectMusicTrigger(startParams);
		case "METALRAILING00":
		case "METALHATCH00B":
		case "METALHATCH00A":
			return new ObjectDestructible(startParams, "", "MetalDebris00A", "MetalDebris00B");
		case "PLAYER":
			return new ObjectPlayer(startParams);
		case "BGPLAYERPORTRAIT00":
			return new ObjectPlayerPortrait(startParams);
		case "TRASHCAN00":
			return new ObjectDestructible(startParams, "Trashcan00_D");
		case "AREATRIGGER":
			return new ObjectAreaTrigger(startParams);
		case "BGNEONCHINESE00":
		case "BGNEONCHINESE01":
			return new ObjectNeon(startParams);
		case "WPNBAZOOKA":
			return new ObjectWeaponItem(startParams, 17);
		case "ACTIVATETRIGGER":
			return new ObjectActivateTrigger(startParams);
		case "CAMERA00_D":
			return new ObjectElectric(startParams, "", "MetalDebris00B", "MetalDebris00C");
		case "DESKLAMP00":
			return new ObjectElectric(startParams);
		case "WIRINGTUBE00A":
			return new ObjectDestructible(startParams, "WiringTube00A_D");
		case "ENABLETRIGGER":
			return new ObjectEnableTrigger(startParams);
		case "WORLDLAYER":
			return new ObjectWorldLayer(startParams);
		case "GASLAMP00":
			return new ObjectExplosive(startParams, "GasLamp00_D")
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreFeetPerformArm
			};
		case "CAMERAAREATRIGGER":
			return new ObjectCameraAreaTrigger(startParams);
		case "WPNDARKSHOTGUN":
			return new ObjectWeaponItem(startParams, 54);
		case "RAILATTACHMENTJOINT":
			return new ObjectRailAttachmentJoint(startParams);
		case "PULLEYENDJOINT":
			return new ObjectPulleyEndJoint(startParams);
		case "CONVEYORBELTVALUETRIGGER":
			return new ObjectConveyorBeltValueTrigger(startParams);
		case "BGMIST00":
			return new ObjectLooping(startParams, new Microsoft.Xna.Framework.Vector2(0.0125f, 0f));
		case "CANNONTURRET00BARREL":
			return new ObjectDestructible(startParams, "CannonTurret00Barrel_D");
		case "CHANDELIER01C":
		case "CHANDELIER01D":
		case "CHANDELIER01B":
			return new ObjectDestructible(startParams, "", "GlassShard00A", "GlassShard00A", "GlassShard00A");
		case "SPAWNFIRECIRCLETRIGGER":
			return new ObjectSpawnFireCircleTrigger(startParams);
		case "PALLET00":
		case "DESK00":
		case "PLANK01":
		case "PLANK02":
		case "PLANK00":
		case "CRATE00":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C");
		case "SSSFLAG00A":
			return new ObjectDestructible(startParams, "SSSFlag00A_D")
			{
				ProjectileTunnelingCheck = ProjectileTunnelingCheck.IgnoreAll
			};
		case "METALDESK00":
		case "TRASHCAN00_D":
		case "BARREL00":
		case "METALTABLE00":
			return new ObjectDestructible(startParams, "", "MetalDebris00A", "MetalDebris00B", "MetalDebris00C")
			{
				BotAIForceRegisterCoverCollision = true
			};
		case "ERROR":
			return new ObjectError(startParams);
		case "CRATE01":
			return new ObjectDestructible(startParams, "", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C", "WoodDebris00D", "WoodDebris00E");
		case "PAPERLANTERN00":
			return new ObjectExplosive(startParams);
		case "CHAIR00":
			return new ObjectWeaponItem(startParams, 32);
		case "WORLD":
			return new ObjectWorld(startParams);
		case "TEAPOT00":
			return new ObjectWeaponItem(startParams, 50);
		case "CAMERA00":
			return new ObjectElectric(startParams, "Camera00_D", "MetalDebris00A");
		case "STREETSWEEPER":
			return new ObjectStreetsweeper(startParams);
		case "SPAWNFIRENODETRIGGER":
			return new ObjectSpawnFireNodeTrigger(startParams);
		case "CUESTICK00SHAFT":
			return new ObjectWeaponItem(startParams, 37);
		case "WPNMP50":
			return new ObjectWeaponItem(startParams, 55);
		case "WPNFLAREGUN":
			return new ObjectWeaponItem(startParams, 27);
		case "WPNSHURIKEN":
			return new ObjectWeaponItem(startParams, 45);
		case "WELDJOINT":
			return new ObjectWeldJoint(startParams);
		case "WOODLATTICE00A":
		case "GLASSSHEET00F":
		case "GLASSSHEET00D":
		case "GLASSSHEET00E":
		case "GLASSSHEET00C":
		case "GLASSSHEET00B":
		case "GLASSSHEET00A":
			return new ObjectGlass(startParams);
		case "BAMBOOSCAFFOLD00C":
		case "BAMBOOSCAFFOLD00B":
		case "BAMBOOSCAFFOLD00E":
		case "BAMBOOSCAFFOLD00D":
			return new ObjectBambooScaffold(startParams);
		case "LAMP01_D":
			return new ObjectElectric(startParams);
		case "STEAMSPAWNER":
		case "BGSTEAMSPAWNER":
			return new ObjectSteamSpawner(startParams);
		case "CAGE00":
			return new ObjectDestructible(startParams, "Cage00_D", "MetalDebris00A", "MetalDebris00B");
		case "CHECKMAPWAVETRIGGER":
			return new ObjectCheckMapWaveTrigger(startParams);
		case "LIGHTSIGN01O_D":
			return new ObjectElectric(startParams);
		case "SEARCHLIGHT00FIXED":
			return new ObjectSearchLight(startParams, autoRotate: false);
		case "WPNKNIFE":
			return new ObjectWeaponItem(startParams, 49);
		case "TEXT":
		case "BGTEXT":
			return new ObjectText(startParams);
		case "PULLJOINT":
			return new ObjectPullJoint(startParams);
		case "STARTUPTRIGGER":
			return new ObjectStartupTrigger(startParams);
		case "METALSCAFFOLD00A":
			return new ObjectDestructible(startParams, "MetalScaffold00A_D", "WoodDebris00A", "WoodDebris00B", "WoodDebris00C");
		case "FARBGCLOCK00":
		case "BGCLOCK00":
		case "BGCLOCK01":
			return new ObjectClockFace(startParams);
		case "GARGOYLE01":
			return new ObjectDestructible(startParams, "Gargoyle01_D", "", "", "", "", "", "", "", "Gargoyle01Head");
		case "SPOTLIGHT00A":
			return new ObjectElectric(startParams, "LightShaftSpotlight", new Microsoft.Xna.Framework.Vector2(4f, -6f), "Spotlight00A_D");
		case "SHELLBIG":
		case "SHELLGLAUNCHER":
		case "MAGSMALL":
		case "SHELLSMALL":
		case "MAGSMG":
		case "MAGASSAULTRIFLE":
		case "WPNGRENADEPIN":
		case "SHELLSHOTGUN":
		case "MAGDRUM":
			return new ObjectShell(startParams);
		case "GARGOYLE00":
			return new ObjectDestructible(startParams, "Gargoyle00_D");
		case "PROJECTILEDEFLECTZONE":
			return new ObjectProjectileDeflectZone(startParams);
		case "WPNPIPEWRENCH":
			return new ObjectWeaponItem(startParams, 4);
		case "BGNEONJOS01":
		case "BGNEONJOS00":
			return new ObjectNeon(startParams);
		case "PAPERBINDER00":
		case "PAPERSTACK00":
			return new ObjectPaperStack(startParams);
		case "HANGINGLAMP00_D":
			return new ObjectElectric(startParams);
		case "ONUPDATETRIGGER":
			return new ObjectOnUpdateTrigger(startParams);
		case "POPUPMESSAGETRIGGER":
			return new ObjectPopupMessageTrigger(startParams);
		case "INVISIBLEBLOCKSMALL":
		case "LINEOFSIGHTBLOCKER":
		case "INVISIBLEBLOCK":
		case "INVISIBLEBLOCKNOCOLLISION":
		case "INVISIBLEEXPLOSIONBLOCKER":
		case "INVISIBLELADDER":
		case "INVISIBLEPLATFORM":
		case "INVISIBLEBLOCKOBJECTSONLY":
		case "DESTROYNODE":
			return new ObjectInvisible(startParams);
		case "LIGHTSIGN00O_D":
			return new ObjectElectric(startParams, "", "LightSign00Debris1", "LightSign00Debris1", "LightSign00Debris1");
		case "BGVENDINGMACHINE01A":
			return new ObjectElectric(startParams, "BgVendingMachine01A_D");
		case "WPNMACHETE":
			return new ObjectWeaponItem(startParams, 8);
		case "BGVENDINGMACHINE01B":
			return new ObjectElectric(startParams, "BgVendingMachine01B_D");
		case "DISABLETRIGGER":
			return new ObjectDisableTrigger(startParams);
		case "SPAWNUNKNOWN":
			return new ObjectSpawnUnknown(startParams);
		default:
			return new ObjectDefault(startParams);
		case "WPNLEADPIPE":
			return new ObjectWeaponItem(startParams, 56);
		}
	}

	public static void CreateNewCompleted(ObjectData objectData)
	{
		if (objectData.GameOwner != GameOwnerEnum.Server && (objectData.MapObjectID.StartsWith("WOODDEBRIS") || objectData.MapObjectID.StartsWith("STONEDEBRIS")))
		{
			EffectHandler.PlayEffect("TR_SPR", Microsoft.Xna.Framework.Vector2.Zero, objectData.GameWorld, objectData.ObjectID, "TR_D", 2f);
		}
	}
}
