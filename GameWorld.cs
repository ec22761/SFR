using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Box2D.XNA;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Networking.LidgrenAdapter;
using SFD.Code;
using SFD.Core;
using SFD.Dialogues;
using SFD.Effects;
using SFD.Fire;
using SFD.GUI;
using SFD.GUI.Text;
using SFD.Gibbing;
using SFD.Input;
using SFD.Logging;
using SFD.MapEditor;
using SFD.Materials;
using SFD.Objects;
using SFD.Parser;
using SFD.PathGrid;
using SFD.Projectiles;
using SFD.Sounds;
using SFD.States;
using SFD.Tiles;
using SFD.UserProgression;
using SFD.Weapons;
using SFD.Weather;
using SFDGameScriptInterface;
using ScriptEngine;
using ScriptEngine.Ros;

namespace SFD;

public class GameWorld
{
	public class SelectionFilter
	{
		public bool ExcludeEditorTiles { get; set; }

		public int SelfTarget { get; set; }

		public bool TargetSelf { get; set; }

		public HashSet<string> Targets { get; set; }

		public HashSet<string> AdditionalTargets { get; set; }

		public HashSet<int> ExcludedCategoryTypes { get; set; }

		public SelectionFilter()
		{
			Targets = new HashSet<string>();
			AdditionalTargets = new HashSet<string>();
			ExcludedCategoryTypes = new HashSet<int>();
			Clear();
		}

		public void Clear()
		{
			SelfTarget = 0;
			TargetSelf = true;
			ExcludeEditorTiles = false;
			Targets.Clear();
			AdditionalTargets.Clear();
			ExcludedCategoryTypes.Clear();
		}

		public bool CheckTargetIncluded(ObjectData objectData)
		{
			if (objectData == null)
			{
				return false;
			}
			if (!TargetSelf && objectData.ObjectID == SelfTarget)
			{
				return false;
			}
			if (Targets.Count > 0)
			{
				return Targets.Contains(objectData.MapObjectID);
			}
			if (AdditionalTargets.Count > 0 && AdditionalTargets.Contains(objectData.MapObjectID))
			{
				return true;
			}
			if (ExcludeEditorTiles && objectData.EditorOnly)
			{
				return false;
			}
			if (objectData.Tile != null && ExcludedCategoryTypes.Contains(objectData.Tile.DrawCategory))
			{
				return false;
			}
			return true;
		}
	}

	public enum EditHistoryLayerAction
	{
		Rename,
		Remove,
		Copy,
		Add,
		MoveUp,
		MoveDown
	}

	public enum EditHistoryObjectAction
	{
		Movement,
		Deletion,
		Creation,
		ChangeZOrder,
		PropertyValueChange,
		ColorValueChange,
		Rotation,
		Flip,
		GroupChange,
		LayerChange
	}

	public abstract class EditHistoryItemBase
	{
		public enum HistoryType
		{
			None,
			Object,
			Layer
		}

		public HistoryType Type { get; set; }

		public EditHistoryItemBase()
		{
		}
	}

	public class EditHistoryItemLayer : EditHistoryItemBase
	{
		public int CategoryIndex { get; set; }

		public int LayerIndex { get; set; }

		public EditHistoryLayerAction Action { get; set; }

		public EditHistoryItemLayerData DataBefore { get; set; }

		public EditHistoryItemLayerData DataAfter { get; set; }

		public EditHistoryItemLayer(int categoryIndex, int layerIndex, EditHistoryLayerAction action, EditHistoryItemLayerData dataBefore, EditHistoryItemLayerData dataAfter)
		{
			CategoryIndex = categoryIndex;
			LayerIndex = layerIndex;
			Action = action;
			DataBefore = dataBefore;
			DataAfter = dataAfter;
			base.Type = HistoryType.Layer;
		}
	}

	public class EditHistoryItemObject : EditHistoryItemBase
	{
		public object[] Args { get; set; }

		public int ObjectId { get; set; }

		public int CustomId { get; set; }

		public string MapObjectId { get; set; }

		public EditHistoryObjectAction Action { get; set; }

		public EditHistoryItemObjectData DataBefore { get; set; }

		public EditHistoryItemObjectData DataAfter { get; set; }

		public EditHistoryItemObject(ObjectData od, EditHistoryObjectAction action, EditHistoryItemObjectData dataBefore, EditHistoryItemObjectData dataAfter)
		{
			ObjectId = od.ObjectID;
			CustomId = od.CustomID;
			MapObjectId = od.MapObjectID;
			Action = action;
			DataBefore = dataBefore;
			DataAfter = dataAfter;
			base.Type = HistoryType.Object;
			if (!(MapObjectId == "WORLD") && MapObjectId == "WORLDLAYER")
			{
				int categoryIndex = -1;
				int layerIndex = -1;
				od.GameWorld.EditGetLayerTag(od, ref categoryIndex, ref layerIndex);
				if (categoryIndex == -1)
				{
					throw new Exception("GameWorld.EditGetLayerTag returned -1, this should not happen!");
				}
				Args = new object[2] { categoryIndex, layerIndex };
			}
		}
	}

	public class EditHistoryItemLayerData
	{
		public int CategoryIndex { get; set; }

		public int LayerIndex { get; set; }

		public string Name { get; set; }

		public object[] Properties { get; set; }

		public EditHistoryItemLayerData(int categoryIndex, int layerIndex, string name, object[] properties)
		{
			CategoryIndex = categoryIndex;
			LayerIndex = layerIndex;
			Name = name;
			Properties = properties;
		}
	}

	public class EditHistoryItemObjectData
	{
		public Microsoft.Xna.Framework.Vector2 Position { get; set; }

		public float Angle { get; set; }

		public int ZOrder { get; set; }

		public short FaceDirection { get; set; }

		public int LocalDrawCategory { get; set; }

		public int LocalLayerIndex { get; set; }

		public string[] ColorNames { get; set; }

		public object[] Properties { get; set; }

		public ushort GroupID { get; set; }

		public EditHistoryItemObjectData(ObjectData od)
		{
			if (od == null)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "EditHistoryItemObjectData od is null");
			}
			SetValuesFromObject(od);
		}

		public void SetValuesFromObject(ObjectData od)
		{
			if (od.Body != null)
			{
				Position = od.Body.GetPosition();
				Angle = od.Body.GetAngle();
			}
			else
			{
				Position = Microsoft.Xna.Framework.Vector2.Zero;
				Angle = 0f;
			}
			if (od.Tile != null && od.LocalRenderLayer != -1)
			{
				ZOrder = od.GetZOrder();
			}
			else
			{
				ZOrder = -1;
			}
			FaceDirection = od.FaceDirection;
			ColorNames = od.ColorsCopy;
			Properties = od.GetProperties().ToObjectArray();
			LocalDrawCategory = od.LocalDrawCategory;
			LocalLayerIndex = od.LocalRenderLayer;
			GroupID = od.GroupID;
		}
	}

	public enum EditRemoveLayerSource
	{
		User,
		History
	}

	public enum EditCopyLayerSource
	{
		User,
		History
	}

	public class MapPartHeaderInfo
	{
		public byte[] ThumbnailData { get; set; }

		public Guid OriginalGuid { get; set; }

		public string OwnerHash { get; set; }

		public MapPartInfo[] Parts { get; set; }

		public void Dispose()
		{
			ThumbnailData = null;
			Parts = null;
		}
	}

	public enum EditCheckLayersEnabledForEditMode
	{
		Edit,
		GroupSelection
	}

	public class EditSearchResult
	{
		public GameWorld SourceGameWorld;

		public string SourceSearchValue;

		public List<EditSearchResultItem> Result;

		public EditSearchResult(GameWorld gameWorld, string searchValue)
		{
			SourceGameWorld = gameWorld;
			SourceSearchValue = searchValue;
			Result = new List<EditSearchResultItem>();
		}

		public override string ToString()
		{
			return SourceSearchValue;
		}
	}

	public enum EditSearchResultMatchType
	{
		Full,
		Partially
	}

	public class EditSearchResultItem
	{
		public ObjectData SourceObject { get; set; }

		public string DisplayPropertyName { get; set; }

		public string DisplayPropertyValue { get; set; }

		public EditSearchResultMatchType MatchType { get; set; }

		public EditSearchResultItem(ObjectData od, string displayPropertyName, string displayPropertyValue, EditSearchResultMatchType matchType)
		{
			SourceObject = od;
			DisplayPropertyName = displayPropertyName;
			DisplayPropertyValue = displayPropertyValue;
			MatchType = matchType;
		}

		public override string ToString()
		{
			if (SourceObject.IsDisposed)
			{
				return "SourceObject disposed";
			}
			if (SourceObject.GroupID > 0)
			{
				return string.Format("(Group {0}) {1} ({2}): '{3}' = {4}", new object[5] { SourceObject.GroupID, SourceObject.MapObjectID, SourceObject.ObjectID, DisplayPropertyName, DisplayPropertyValue });
			}
			return string.Format("{0} ({1}): '{2}' = {3}", new object[4] { SourceObject.MapObjectID, SourceObject.ObjectID, DisplayPropertyName, DisplayPropertyValue });
		}
	}

	public struct SelectionLine(Microsoft.Xna.Framework.Vector2 start, Microsoft.Xna.Framework.Vector2 end)
	{
		public Microsoft.Xna.Framework.Vector2 Start = start;

		public Microsoft.Xna.Framework.Vector2 End = end;
	}

	public class SelectionArea
	{
		public Microsoft.Xna.Framework.Vector2 Start { get; set; }

		public Microsoft.Xna.Framework.Vector2 End { get; set; }

		public bool IsValidSelection => End != Start;

		public FixedArray4<SelectionLine> GetSelectionLines()
		{
			return new FixedArray4<SelectionLine>
			{
				[0] = new SelectionLine(new Microsoft.Xna.Framework.Vector2(Start.X, Start.Y), new Microsoft.Xna.Framework.Vector2(End.X, Start.Y)),
				[1] = new SelectionLine(new Microsoft.Xna.Framework.Vector2(End.X, Start.Y), new Microsoft.Xna.Framework.Vector2(End.X, End.Y)),
				[2] = new SelectionLine(new Microsoft.Xna.Framework.Vector2(End.X, End.Y), new Microsoft.Xna.Framework.Vector2(Start.X, End.Y)),
				[3] = new SelectionLine(new Microsoft.Xna.Framework.Vector2(Start.X, End.Y), new Microsoft.Xna.Framework.Vector2(Start.X, Start.Y))
			};
		}

		public Microsoft.Xna.Framework.Vector2 GetTopRight()
		{
			return new Microsoft.Xna.Framework.Vector2((Start.X > End.X) ? Start.X : End.X, (Start.Y > End.Y) ? Start.Y : End.Y);
		}

		public Microsoft.Xna.Framework.Vector2 GetBottomLeft()
		{
			return new Microsoft.Xna.Framework.Vector2((Start.X < End.X) ? Start.X : End.X, (Start.Y < End.Y) ? Start.Y : End.Y);
		}

		public bool Contains(Microsoft.Xna.Framework.Vector2 point)
		{
			Microsoft.Xna.Framework.Vector2 bottomLeft = GetBottomLeft();
			if (point.X < bottomLeft.X)
			{
				return false;
			}
			if (point.Y < bottomLeft.Y)
			{
				return false;
			}
			bottomLeft = GetTopRight();
			if (point.Y > bottomLeft.Y)
			{
				return false;
			}
			if (point.X > bottomLeft.X)
			{
				return false;
			}
			return true;
		}

		public void Reset()
		{
			Microsoft.Xna.Framework.Vector2 start = (End = Microsoft.Xna.Framework.Vector2.Zero);
			Start = start;
		}

		public SelectionArea()
		{
			Reset();
		}
	}

	public struct LayerStatus(bool isLocked, bool isVisisble)
	{
		public bool IsLocked = isLocked;

		public bool IsVisible = isVisisble;

		public override string ToString()
		{
			return $"IsLocked={IsLocked}, IsVisisble={IsVisible}";
		}
	}

	public class EditSelectionGroupItem
	{
		public ObjectData ObjectData;

		public LayerStatus LayerStatus;

		public EditSelectionGroupItem(ObjectData objectData, LayerStatus layerStatus)
		{
			ObjectData = objectData;
			LayerStatus = layerStatus;
		}
	}

	public class EditSelectionGroupInfo
	{
		public List<EditSelectionGroupItem> Items;

		public EditSelectionGroupInfo()
		{
			Items = new List<EditSelectionGroupItem>();
		}

		public void AddObject(ObjectData objectData, LayerStatus layerStatus)
		{
			Items.Add(new EditSelectionGroupItem(objectData, layerStatus));
		}

		public bool ContainsLockedOrHiddenLayer()
		{
			foreach (EditSelectionGroupItem item in Items)
			{
				if (item.LayerStatus.IsLocked | !item.LayerStatus.IsVisible)
				{
					return true;
				}
			}
			return false;
		}

		public List<ObjectData> GetAllObjects()
		{
			List<ObjectData> list = new List<ObjectData>();
			foreach (EditSelectionGroupItem item in Items)
			{
				list.Add(item.ObjectData);
			}
			return list;
		}
	}

	[Flags]
	public enum EditSelectedObjectsOptions
	{
		None = 0,
		GroupCheck = 1
	}

	public enum EditChangeSelectionInWindowType
	{
		Add,
		Remove
	}

	public class SFDLayerFarBGTag : SFDLayerTag
	{
		public float FarBackgroundMovementFactor { get; set; }

		public bool FarBackgroundBorderEnabled { get; set; }

		public int FarBackgroundBorderPosition { get; set; }

		public SFDLayerFarBGTag()
		{
			FarBackgroundMovementFactor = 1f;
			FarBackgroundBorderEnabled = false;
			FarBackgroundBorderPosition = 0;
		}
	}

	public class SFDLayerTag : IDisposable
	{
		public ObjectData Object { get; set; }

		public ObjectProperties Properties => Object.Properties;

		~SFDLayerTag()
		{
		}

		public void Dispose()
		{
			if (Object != null)
			{
				Object.Dispose();
				Object = null;
			}
		}
	}

	public abstract class PlayerAIPackage
	{
		public ObjectData m_ownerObject;

		public Player m_owner;

		public GameWorld GameWorld => OwnerObject.GameWorld;

		public ObjectData OwnerObject
		{
			get
			{
				return m_ownerObject;
			}
			set
			{
				m_ownerObject = value;
				m_owner = ((value == null || !value.IsPlayer) ? null : ((Player)value.InternalData));
			}
		}

		public Player Owner
		{
			get
			{
				return m_owner;
			}
			set
			{
				m_owner = value;
				m_ownerObject = value?.ObjectData;
			}
		}

		public int ProcessedCount { get; set; }

		public float LastProcessTimestamp { get; set; }

		public bool IsQueued { get; set; }

		public bool ForceAnotherProcessPass { get; set; }

		public PlayerAIPackage()
		{
			ProcessedCount = 0;
			LastProcessTimestamp = -9999f;
			ForceAnotherProcessPass = false;
		}

		public abstract void Process();

		public abstract void ClearData();

		public void Requeue(float delayTime = 0f)
		{
			if (!IsQueued && GameWorld.ElapsedTotalRealTime > LastProcessTimestamp + delayTime)
			{
				GameWorld.PlayerAIPackages.Enqueue(this);
				IsQueued = true;
			}
		}
	}

	public abstract class PlayerAIPackageTargetObject : PlayerAIPackage
	{
		public ObjectData ResultTarget { get; set; }

		public override void ClearData()
		{
			ResultTarget = null;
		}

		public PlayerAIPackageTargetObject()
		{
		}
	}

	public class PlayerAIPackageSearchTargetObject : PlayerAIPackageTargetObject
	{
		public ObjectData PrimaryEnemy { get; set; }

		public ObjectData SearchItem { get; set; }

		public bool HasPrimaryTarget { get; set; }

		public int PrimaryEnemyCachedTeamTargetCount { get; set; }

		public override void ClearData()
		{
			SearchItem = null;
			PrimaryEnemy = null;
			HasPrimaryTarget = false;
			PrimaryEnemyCachedTeamTargetCount = 0;
			base.ClearData();
		}

		public void ForceSearchItem(ObjectData value)
		{
			SearchItem = value;
		}

		public override void Process()
		{
			if (base.Owner != null && !base.Owner.IsDisposed)
			{
				if (PrimaryEnemy != null && PrimaryEnemy.IsDisposed)
				{
					PrimaryEnemy = null;
					HasPrimaryTarget = false;
				}
				if (SearchItem != null && SearchItem.IsDisposed)
				{
					SearchItem = null;
				}
				ObjectData objectData = null;
				_ = Microsoft.Xna.Framework.Vector2.Zero;
				float num = 0f;
				Microsoft.Xna.Framework.Vector2 vector = Microsoft.Xna.Framework.Vector2.Zero;
				ObjectData activeGuardTarget = base.Owner.GetActiveGuardTarget();
				if (activeGuardTarget != null)
				{
					vector = activeGuardTarget.GetWorldCenterPosition();
				}
				HasPrimaryTarget = false;
				bool flag = false;
				Dictionary<int, int> dictionary = TeamTargetedObjectsCount();
				if (base.Owner.BotBehaviorSet.EliminateEnemies)
				{
					Player player = null;
					_ = Microsoft.Xna.Framework.Vector2.Zero;
					float num2 = 0f;
					int primaryEnemyCachedTeamTargetCount = 0;
					foreach (Player player2 in base.GameWorld.Players)
					{
						if (player2.IsDisposed || !player2.IsEnemyOf(base.Owner) || !player2.IsValidBotEliminateTarget || player2.ObjectData == activeGuardTarget)
						{
							continue;
						}
						Microsoft.Xna.Framework.Vector2 vector2 = player2.PreWorld2DPosition - base.Owner.PreWorld2DPosition;
						float num3 = vector2.CalcSafeLength();
						if (!player2.IsDead)
						{
							if ((num3 > 40f || PrimaryEnemy != player2.ObjectData) && base.Owner.BotBehaviorSet.AggroRange > 0f && num3 > base.Owner.BotBehaviorSet.AggroRange && (PrimaryEnemy != player2.ObjectData || num3 - 150f > base.Owner.BotBehaviorSet.AggroRange))
							{
								continue;
							}
							if (activeGuardTarget != null)
							{
								float num4 = (player2.PreWorld2DPosition - vector).CalcSafeLength();
								if (PrimaryEnemy == player2.ObjectData)
								{
									num4 += 24f;
								}
								if (base.Owner.BotBehaviorSet.ChaseRange > 0f && num4 > base.Owner.BotBehaviorSet.ChaseRange)
								{
									num4 *= 0.33f;
									if (PrimaryEnemy == player2.ObjectData && num4 > base.Owner.BotBehaviorSet.ChaseRange && num4 > base.Owner.BotBehaviorSet.GuardRange)
									{
										flag = true;
									}
									continue;
								}
							}
							int value = 0;
							if (dictionary != null && !dictionary.TryGetValue(player2.ObjectID, out value))
							{
								value = 0;
							}
							if (player2.ObjectData == PrimaryEnemy)
							{
								if (PrimaryEnemyCachedTeamTargetCount > value)
								{
									PrimaryEnemyCachedTeamTargetCount = value;
								}
								value = PrimaryEnemyCachedTeamTargetCount;
							}
							if (player2.BotAICheckOpponentTargetLockedToEachOther)
							{
								value = Math.Max(value, 2);
							}
							if (num3 > 40f || value > 2)
							{
								num3 += Math.Min((float)value * 40f, 200f);
							}
							if (PrimaryEnemy != player2.ObjectData && !player2.IsUserControlled && value > 1)
							{
								num3 += Math.Min((float)value * 40f, 200f);
							}
							if (player2.RocketRideProjectileWorldID != 0 && num3 > 8f)
							{
								Projectile rocketRideProjectile = player2.RocketRideProjectile;
								if (rocketRideProjectile != null && Microsoft.Xna.Framework.Vector2.Dot(Microsoft.Xna.Framework.Vector2.Normalize(vector2), rocketRideProjectile.Direction) > -0.2f)
								{
									num3 += 100f;
								}
							}
							if (SFDMath.IsValid(player2.AITargetData.Range) && player2.AITargetData.Range > 0f && num3 - ((PrimaryEnemy == player2.ObjectData) ? 100f : 0f) > player2.AITargetData.Range)
							{
								continue;
							}
							if (SFDMath.IsValid(player2.AITargetData.PriorityModifier))
							{
								if (player2.AITargetData.PriorityModifier <= 0f)
								{
									continue;
								}
								num3 *= 1f / player2.AITargetData.PriorityModifier;
							}
							if ((player2.AITargetData.TargetMode != ObjectAITargetMode.RangeOnly || base.Owner.BotAICanUseRangedAndHasAmmo()) && (player2.AITargetData.TargetMode != ObjectAITargetMode.MeleeOnly || base.Owner.BotBehaviorSet.MeleeUsage) && (objectData == null || num3 < num))
							{
								objectData = player2.ObjectData;
								num = num3;
								primaryEnemyCachedTeamTargetCount = value;
							}
						}
						else if (player == null || num3 < num2)
						{
							player = player2;
							num2 = num3;
						}
					}
					foreach (ObjectData aITargetableObject in base.GameWorld.m_AITargetableObjects)
					{
						if (aITargetableObject.IsDisposed || aITargetableObject is ObjectPlayer)
						{
							continue;
						}
						bool flag2 = false;
						_ = aITargetableObject.AITargetData;
						flag2 = ((!(aITargetableObject is ObjectStreetsweeper)) ? (aITargetableObject.AITargetData.Team == PlayerTeam.Independent || aITargetableObject.AITargetData.Team != (PlayerTeam)base.Owner.CurrentTeam) : ((ObjectStreetsweeper)aITargetableObject).IsEnemyOf(base.Owner));
						if (!(!aITargetableObject.IsDisposed && flag2))
						{
							continue;
						}
						Microsoft.Xna.Framework.Vector2 x = aITargetableObject.GetWorldCenterPosition() - base.Owner.PreWorld2DPosition;
						float num5 = x.CalcSafeLength();
						if ((num5 > 40f || PrimaryEnemy != aITargetableObject) && base.Owner.BotBehaviorSet.AggroRange > 0f && num5 > base.Owner.BotBehaviorSet.AggroRange && (PrimaryEnemy != aITargetableObject || num5 - 150f > base.Owner.BotBehaviorSet.AggroRange))
						{
							continue;
						}
						if (activeGuardTarget != null)
						{
							float num6 = (aITargetableObject.GetWorldCenterPosition() - vector).CalcSafeLength();
							if (PrimaryEnemy == aITargetableObject)
							{
								num6 += 24f;
							}
							if (base.Owner.BotBehaviorSet.ChaseRange > 0f && num6 > base.Owner.BotBehaviorSet.ChaseRange)
							{
								num6 *= 0.33f;
								if (PrimaryEnemy == aITargetableObject && num6 > base.Owner.BotBehaviorSet.ChaseRange && num6 > base.Owner.BotBehaviorSet.GuardRange)
								{
									flag = true;
								}
								continue;
							}
						}
						if (SFDMath.IsValid(aITargetableObject.AITargetData.Range) && aITargetableObject.AITargetData.Range > 0f && num5 - ((PrimaryEnemy == aITargetableObject) ? 100f : 0f) > aITargetableObject.AITargetData.Range)
						{
							continue;
						}
						if (SFDMath.IsValid(aITargetableObject.AITargetData.PriorityModifier))
						{
							if (aITargetableObject.AITargetData.PriorityModifier <= 0f)
							{
								continue;
							}
							num5 *= 1f / aITargetableObject.AITargetData.PriorityModifier;
						}
						if ((aITargetableObject.AITargetData.TargetMode != ObjectAITargetMode.RangeOnly || base.Owner.BotAICanUseRangedAndHasAmmo()) && (aITargetableObject.AITargetData.TargetMode != ObjectAITargetMode.MeleeOnly || base.Owner.BotBehaviorSet.MeleeUsage) && (objectData == null || num5 < num))
						{
							objectData = aITargetableObject;
							num = num5;
							primaryEnemyCachedTeamTargetCount = 1;
						}
					}
					if (objectData == null && base.Owner.BotBehaviorSet.AttackDeadEnemies)
					{
						objectData = player?.ObjectData;
						num = num2;
						primaryEnemyCachedTeamTargetCount = 0;
					}
					if ((objectData != null || PrimaryEnemy == activeGuardTarget || flag) && PrimaryEnemy != objectData)
					{
						PrimaryEnemy = objectData;
						PrimaryEnemyCachedTeamTargetCount = primaryEnemyCachedTeamTargetCount;
					}
					else if (!base.Owner.BotBehaviorSet.AttackDeadEnemies && PrimaryEnemy != null && !PrimaryEnemy.IsDisposed && PrimaryEnemy.IsPlayer && ((Player)PrimaryEnemy.InternalData).IsDead)
					{
						PrimaryEnemy = null;
						PrimaryEnemyCachedTeamTargetCount = 0;
					}
					if (num > 100f)
					{
						num *= 1.5f;
					}
				}
				else
				{
					PrimaryEnemy = null;
					PrimaryEnemyCachedTeamTargetCount = 0;
				}
				bool flag3 = false;
				List<Player> realUserTeammates = null;
				ObjectData objectData2 = null;
				float num7 = 0f;
				if (base.Owner.BotBehaviorSet.SearchForItems)
				{
					foreach (ObjectData botSearchItem in base.GameWorld.BotSearchItems)
					{
						if ((dictionary != null && dictionary.ContainsKey(botSearchItem.ObjectID)) || base.Owner.m_botAIUnavailableTargets.Contains(botSearchItem.ObjectID))
						{
							continue;
						}
						if (!flag3)
						{
							realUserTeammates = base.Owner.BotAIGetRealUserTeammates();
							flag3 = true;
						}
						float distance = Converter.Box2DToWorld(base.Owner.PreBox2DPosition - botSearchItem.GetBox2DPosition()).CalcSafeLength();
						bool isMakeshiftWeapon = false;
						bool ignoreExistTime = SearchItem == botSearchItem;
						distance = CheckSearchItemRevelant(botSearchItem, objectData, realUserTeammates, distance, ignoreExistTime, out isMakeshiftWeapon);
						if (!float.IsNaN(distance) && (!(base.Owner.BotBehaviorSet.SearchItemRange > 0f) || !(distance > base.Owner.BotBehaviorSet.SearchItemRange) || (SearchItem == objectData2 && !(distance - 300f > base.Owner.BotBehaviorSet.SearchItemRange))) && (activeGuardTarget == null || !((botSearchItem.GetWorldCenterPosition() - vector).CalcSafeLength() > base.Owner.BotBehaviorSet.ChaseRange)) && (objectData2 == null || distance < num7))
						{
							if (base.GameWorld.PathGrid.FindClosestPathNodeBelow(botSearchItem.GetBox2DCenterPosition(), mustHaveOutgoingConnections: false, 1.1999999f, 1.28f) != null)
							{
								objectData2 = botSearchItem;
								num7 = distance;
							}
							else if (isMakeshiftWeapon)
							{
								base.Owner.AddAIUnavailableTarget(null, botSearchItem.ObjectID);
							}
						}
					}
					SearchItem = objectData2;
				}
				else
				{
					SearchItem = null;
				}
				if (base.Owner.m_botAISeekCoverObject != null && !base.Owner.m_botAISeekCoverObject.IsDisposed)
				{
					base.ResultTarget = base.Owner.m_botAISeekCoverObject;
				}
				else if (SearchItem == null)
				{
					base.ResultTarget = ((PrimaryEnemy != null) ? PrimaryEnemy : null);
				}
				else if (PrimaryEnemy != null && (PrimaryEnemy.IsDisposed || !PrimaryEnemy.IsPlayer || !((Player)PrimaryEnemy.InternalData).IsDead))
				{
					if (num < num7)
					{
						base.ResultTarget = PrimaryEnemy;
					}
					else
					{
						base.ResultTarget = SearchItem;
					}
				}
				else
				{
					base.ResultTarget = SearchItem;
				}
				if (base.ResultTarget == null && activeGuardTarget != null)
				{
					if (base.Owner.BotBehaviorSet.GuardRange < 10f)
					{
						base.ResultTarget = activeGuardTarget;
					}
					else
					{
						PlayerAIPackageGuardPosition playerAIPackage = base.Owner.GetPlayerAIPackage<PlayerAIPackageGuardPosition>();
						playerAIPackage.GuardTarget = activeGuardTarget;
						playerAIPackage.Requeue(((playerAIPackage.GuardPosition == null) | playerAIPackage.IsOld) ? 1f : 300f);
						base.ResultTarget = ((playerAIPackage.GuardPosition != null) ? playerAIPackage.GuardPosition : activeGuardTarget);
					}
				}
				HasPrimaryTarget = base.ResultTarget != null;
			}
			else
			{
				ClearData();
			}
		}

		public Dictionary<int, int> TeamTargetedObjectsCount()
		{
			Dictionary<int, int> dictionary = null;
			if (base.Owner.CurrentTeam != Team.Independent)
			{
				foreach (Player player in base.GameWorld.Players)
				{
					if (player.IsDisposed || player == base.Owner || !base.Owner.InSameTeam(player))
					{
						continue;
					}
					int num = 0;
					int value = 0;
					if (player.BotAITargetOpponent.HasTarget)
					{
						num = player.BotAITargetOpponent.CurrentTarget.ObjectID;
						if (dictionary == null)
						{
							dictionary = new Dictionary<int, int>();
						}
						value = ((!dictionary.TryGetValue(num, out value)) ? 1 : (value + 1));
						dictionary[player.BotAITargetOpponent.CurrentTarget.ObjectID] = value;
					}
					if (player.BotAITargetDestination.HasTarget && num != player.BotAITargetDestination.CurrentTarget.ObjectID)
					{
						num = player.BotAITargetDestination.CurrentTarget.ObjectID;
						if (dictionary == null)
						{
							dictionary = new Dictionary<int, int>();
						}
						value = ((!dictionary.TryGetValue(num, out value)) ? 1 : (value + 1));
						dictionary[player.BotAITargetDestination.CurrentTarget.ObjectID] = value;
					}
				}
			}
			return dictionary;
		}

		public float CheckSearchItemRevelant(ObjectData searchItem, ObjectData primaryEnemey, List<Player> realUserTeammates, float distance, bool ignoreExistTime, out bool isMakeshiftWeapon)
		{
			SFD.Weapons.WeaponItem weaponItem = null;
			isMakeshiftWeapon = false;
			bool flag = false;
			if (searchItem != null && searchItem.MissileData == null)
			{
				if (searchItem is ObjectSupplyCrate)
				{
					if (!((ObjectSupplyCrate)searchItem).BotsCanSearchForThisCrate)
					{
						return float.NaN;
					}
				}
				else if (searchItem is ObjectActivateTrigger)
				{
					if (!((ObjectActivateTrigger)searchItem).ObjectCanActivateThisTrigger(base.Owner.ObjectID))
					{
						return float.NaN;
					}
				}
				else if (searchItem is ObjectStreetsweeperCrate)
				{
					ObjectStreetsweeperCrate objectStreetsweeperCrate = (ObjectStreetsweeperCrate)searchItem;
					if (!objectStreetsweeperCrate.BotsCanSearchForThisCrate || objectStreetsweeperCrate.IsActivatedAndOpening)
					{
						return float.NaN;
					}
				}
				if (realUserTeammates != null && realUserTeammates.Count > 0 && searchItem.TotalExistTime < 4000f && !ignoreExistTime && (!(searchItem is ObjectWeaponItem) || ((ObjectWeaponItem)searchItem).DroppedByPlayerID == 0 || ((ObjectWeaponItem)searchItem).DroppedByPlayerSource != Player.DropWeaponItemSource.ManuallyDropped))
				{
					float num = 6f;
					float num2 = (float)Math.Sqrt(realUserTeammates.Select((Player x) => Microsoft.Xna.Framework.Vector2.DistanceSquared(x.PreBox2DPosition, searchItem.GetBox2DPosition())).Min());
					if (num2 < num)
					{
						float num3 = 1f - num2 / num;
						if (searchItem.TotalExistTime < Math.Max(num3 * 4000f, 1000f))
						{
							return float.NaN;
						}
					}
				}
				if (searchItem is ObjectSupplyCrate)
				{
					weaponItem = ((ObjectSupplyCrate)searchItem).GetInternalItem();
					flag = true;
				}
				else if (searchItem is ObjectWeaponItem)
				{
					if (!ignoreExistTime && searchItem.GetBox2DPosition().Y < base.Owner.PreBox2DPosition.Y && searchItem.Body.IsAwake() && !searchItem.Body.HasTouchingContact(delegate(ContactEdge x)
					{
						x.Contact.GetWorldManifold(out var worldManifold);
						return Microsoft.Xna.Framework.Vector2.Dot(worldManifold._normal, Microsoft.Xna.Framework.Vector2.UnitY) > 0.15f;
					}))
					{
						return float.NaN;
					}
					weaponItem = ((ObjectWeaponItem)searchItem).GetWeaponItem();
				}
				else
				{
					if (searchItem is ObjectMedicalCabinetTrigger)
					{
						if (base.Owner.Health.Fullness > 0.9f)
						{
							return float.NaN;
						}
						if (base.Owner.Health.Fullness <= 0.9f && distance < 80f)
						{
							return distance;
						}
						float val = 1f - base.Owner.Health.Fullness;
						return distance * Math.Max(val, 0.2f);
					}
					if (searchItem is ObjectStreetsweeperCrate && (base.Owner.BotBehaviorSet.SearchItems & SearchItems.Streetsweeper) == SearchItems.Streetsweeper)
					{
						if (distance < 32f)
						{
							return distance * 1.5f;
						}
						return distance * 0.8f;
					}
				}
				if (weaponItem == null)
				{
					return float.NaN;
				}
				isMakeshiftWeapon = weaponItem.BaseProperties.IsMakeshift;
				SearchItems searchItems = base.Owner.BotBehaviorSet.SearchItems;
				Player player = ((primaryEnemey == null || !primaryEnemey.IsPlayer) ? null : ((Player)primaryEnemey.InternalData));
				switch (weaponItem.Type)
				{
				case SFD.Weapons.WeaponItemType.NONE:
					return float.NaN;
				default:
					return float.NaN;
				case SFD.Weapons.WeaponItemType.Handgun:
					if (base.Owner.BotBehaviorSet.RangedWeaponUsage && (searchItems & SearchItems.Secondary) == SearchItems.Secondary && !weaponItem.RWeaponData.IsEmpty)
					{
						if (base.Owner.CurrentHandgunWeapon == null || (base.Owner.CurrentHandgunWeapon.IsEmpty && !base.Owner.CurrentHandgunWeapon.CanBeReloaded(base.Owner)))
						{
							if (player != null && player.GetCurrentRangedWeaponInUse() != null)
							{
								return distance * 0.6f;
							}
							return distance * 0.8f;
						}
						float num4 = base.Owner.CurrentHandgunWeapon.AI_WeaponScore();
						if (weaponItem.RWeaponData.AI_WeaponScore() * 0.7f > num4)
						{
							return distance;
						}
					}
					return float.NaN;
				case SFD.Weapons.WeaponItemType.Rifle:
					if (base.Owner.BotBehaviorSet.RangedWeaponUsage && (searchItems & SearchItems.Primary) == SearchItems.Primary && !weaponItem.RWeaponData.IsEmpty)
					{
						if (base.Owner.CurrentRifleWeapon == null || (base.Owner.CurrentRifleWeapon.IsEmpty && !base.Owner.CurrentRifleWeapon.CanBeReloaded(base.Owner)))
						{
							if (player != null && player.GetCurrentRangedWeaponInUse() != null)
							{
								return distance * 0.6f;
							}
							return distance * 0.8f;
						}
						float num5 = base.Owner.CurrentRifleWeapon.AI_WeaponScore();
						if (weaponItem.RWeaponData.AI_WeaponScore() * 0.7f > num5)
						{
							return distance;
						}
					}
					return float.NaN;
				case SFD.Weapons.WeaponItemType.Thrown:
					return float.NaN;
				case SFD.Weapons.WeaponItemType.Melee:
					if (base.Owner.BotBehaviorSet.MeleeWeaponUsage)
					{
						if (weaponItem.MWeaponData.Properties.IsMakeshift)
						{
							if ((searchItems & SearchItems.Makeshift) == SearchItems.Makeshift && !weaponItem.MWeaponData.Durability.IsEmpty)
							{
								if (weaponItem.BaseProperties.WeaponID == 47 || !(distance <= 120f))
								{
									return float.NaN;
								}
								if (base.Owner.CurrentMeleeMakeshiftWeapon != null && weaponItem.MWeaponData.Properties.AI_DamageOutput < base.Owner.CurrentMeleeMakeshiftWeapon.Properties.AI_DamageOutput)
								{
									return float.NaN;
								}
								if (base.Owner.CurrentMeleeWeapon != null && weaponItem.MWeaponData.Properties.AI_DamageOutput < base.Owner.CurrentMeleeWeapon.Properties.AI_DamageOutput)
								{
									return float.NaN;
								}
								if (base.Owner.CurrentMeleeMakeshiftWeapon != null && distance < 40f && weaponItem.MWeaponData.Properties.DamagePlayers > base.Owner.CurrentMeleeMakeshiftWeapon.Properties.DamagePlayers)
								{
									return distance;
								}
								if (base.Owner.CurrentMeleeWeapon != null)
								{
									if (weaponItem.MWeaponData.PotentialDamageOutput() * 0.5f > base.Owner.CurrentMeleeWeapon.PotentialDamageOutput())
									{
										return distance;
									}
								}
								else
								{
									if (base.Owner.CurrentWeaponDrawn == SFD.Weapons.WeaponItemType.NONE)
									{
										return distance;
									}
									RWeapon currentRangedWeaponInUse = base.Owner.GetCurrentRangedWeaponInUse();
									if (currentRangedWeaponInUse != null && currentRangedWeaponInUse.Properties.AI_DamageOutput < DamageOutputType.High && currentRangedWeaponInUse.PotentialDamageOutput() * 0.6f < weaponItem.MWeaponData.PotentialDamageOutput())
									{
										return distance;
									}
								}
							}
						}
						else if ((searchItems & SearchItems.Melee) == SearchItems.Melee && !weaponItem.MWeaponData.Durability.IsEmpty)
						{
							if (base.Owner.CurrentMeleeWeapon == null)
							{
								if (player != null && player.CurrentMeleeWeapon != null)
								{
									return distance * 0.6f;
								}
								return distance * 0.8f;
							}
							float num6 = weaponItem.MWeaponData.PotentialDamageOutput();
							float num7 = base.Owner.CurrentMeleeWeapon.PotentialDamageOutput();
							if ((weaponItem.MWeaponData.Properties.AI_DamageOutput >= base.Owner.CurrentMeleeWeapon.Properties.AI_DamageOutput && num6 * 0.4f > num7) || (weaponItem.MWeaponData.Properties.AI_DamageOutput > base.Owner.CurrentMeleeWeapon.Properties.AI_DamageOutput && num6 * 1.2f > num7))
							{
								return distance;
							}
						}
					}
					return float.NaN;
				case SFD.Weapons.WeaponItemType.Powerup:
					if (base.Owner.BotBehaviorSet.PowerupUsage && (searchItems & SearchItems.Powerups) == SearchItems.Powerups && weaponItem.BaseProperties.WeaponID == 62 && (base.Owner.CurrentPowerupItem == null || base.Owner.CurrentPowerupItem.Properties.WeaponID == 15 || base.Owner.CurrentPowerupItem.Properties.WeaponID == 16))
					{
						if (distance < 32f)
						{
							return distance;
						}
						return distance * 0.4f;
					}
					return float.NaN;
				case SFD.Weapons.WeaponItemType.InstantPickup:
					if ((searchItems & SearchItems.Health) == SearchItems.Health)
					{
						short weaponID = weaponItem.BaseProperties.WeaponID;
						if ((uint)(weaponID - 13) <= 1u)
						{
							if (base.Owner.Health.Fullness > 0.9f && distance >= 40f)
							{
								return float.NaN;
							}
							if (base.Owner.Health.Fullness <= 0.5f && distance < 60f)
							{
								return distance;
							}
							float val2 = 1f - base.Owner.Health.Fullness;
							return distance * Math.Max(val2, 0.2f) * (flag ? 0.8f : 1f);
						}
					}
					if ((searchItems & SearchItems.Powerups) == SearchItems.Powerups)
					{
						short weaponID = weaponItem.BaseProperties.WeaponID;
						if ((uint)(weaponID - 66) <= 1u && (base.Owner.CurrentRifleWeapon != null || base.Owner.CurrentHandgunWeapon != null))
						{
							if (distance < 32f)
							{
								return distance;
							}
							return distance * 0.4f;
						}
					}
					return float.NaN;
				}
			}
			return float.NaN;
		}
	}

	public class PlayerAIPackageTargetDebugPathFiniding : PlayerAIPackageTargetObject
	{
		public override void Process()
		{
			if (base.Owner != null && !base.Owner.IsDisposed)
			{
				int num = -1;
				ObjectData resultTarget = base.ResultTarget;
				if (resultTarget != null && !resultTarget.IsDisposed && resultTarget is ObjectPathDebugTarget)
				{
					PathNode pathNode = base.Owner.GameWorld.PathGrid.FindClosestPathNodeBelow(resultTarget.GetBox2DCenterPosition(), mustHaveOutgoingConnections: false);
					if (pathNode != null && base.Owner.BotAINav.NodeA != null && base.Owner.BotAINav.NodeA.InstanceID == pathNode.InstanceID)
					{
						num = ((ObjectPathDebugTarget)resultTarget).CurrentDebugIndex + 1;
					}
					else if ((base.Owner.PreBox2DPosition - resultTarget.GetBox2DCenterPosition()).CalcSafeLength() <= 0.64f)
					{
						num = ((ObjectPathDebugTarget)resultTarget).CurrentDebugIndex + 1;
					}
					if (num == -1)
					{
						base.ResultTarget = resultTarget;
						return;
					}
				}
				ObjectPathDebugTarget objectPathDebugTarget = null;
				if (num == -1 || num >= ObjectPathDebugTarget.PathDebugTargets.Count)
				{
					num = 0;
				}
				if (num >= 0 && num < ObjectPathDebugTarget.PathDebugTargets.Count)
				{
					objectPathDebugTarget = ObjectPathDebugTarget.PathDebugTargets[num];
				}
				if (objectPathDebugTarget != null && objectPathDebugTarget.IsDisposed)
				{
					objectPathDebugTarget = null;
				}
				base.ResultTarget = objectPathDebugTarget;
			}
			else
			{
				ClearData();
			}
		}
	}

	public class PlayerAIPackageDiveCheck : PlayerAIPackage
	{
		public ObjectData Target { get; set; }

		public bool CanDive { get; set; }

		public override void Process()
		{
			if (base.Owner != null && !base.Owner.IsDisposed && Target != null && !Target.IsDisposed && Target.IsPlayer)
			{
				bool flag = false;
				Microsoft.Xna.Framework.Vector2 x = ((Player)Target.InternalData).PreBox2DPosition - base.Owner.PreBox2DPosition;
				x = x.Sanitize();
				if (flag = Math.Abs(x.X) < 1.28f && Math.Abs(x.Y) < 0.32f)
				{
					float num = Converter.Box2DToWorld(x.Length());
					if (num > 4f)
					{
						flag = false;
						base.Owner.GetFixtureCircle().GetFilterData(out var plrFilter);
						Microsoft.Xna.Framework.Vector2 direction = Microsoft.Xna.Framework.Vector2.Normalize(x);
						Filter fixtureFilter;
						RayCastResult rayCastResult = base.GameWorld.RayCast(Converter.Box2DToWorld(base.Owner.PreBox2DPosition), direction, 0f, num, delegate(Fixture fixture)
						{
							fixture.GetFilterData(out fixtureFilter);
							return Settings.b2ShouldCollide(ref plrFilter, ref fixtureFilter);
						}, (Player player) => player != base.Owner);
						if (rayCastResult.EndFixture != null && ObjectData.Read(rayCastResult.EndFixture) == Target)
						{
							flag = true;
						}
					}
				}
				CanDive = flag;
			}
			else
			{
				ClearData();
			}
		}

		public override void ClearData()
		{
			Target = null;
			CanDive = false;
		}
	}

	public class PlayerAIPackageFindGuardingRangedTarget : PlayerAIPackage
	{
		public enum StatusType
		{
			Queued,
			None,
			Obstructed,
			ClearLOS
		}

		public float m_losTunnelingDistance;

		public Player m_lastScannedPlayer;

		public int m_lastScannedPlayerIndex = -1;

		public StatusType m_lastScannedPlayerStatus = StatusType.None;

		public float m_lastScannedPlayerTimestamp;

		public Player LOSOpponent { get; set; }

		public Microsoft.Xna.Framework.Vector2 LOSOrigin { get; set; }

		public float LOSTunnelingDistance
		{
			get
			{
				return m_losTunnelingDistance;
			}
			set
			{
				m_losTunnelingDistance = value;
			}
		}

		public StatusType Status => m_lastScannedPlayerStatus;

		public override void Process()
		{
			if (base.Owner != null && !base.Owner.IsDisposed)
			{
				if (LOSOpponent == null || LOSOpponent.IsDisposed || LOSOpponent.IsDead)
				{
					LOSOpponent = null;
				}
				if (m_lastScannedPlayer == null || m_lastScannedPlayer.IsDisposed || m_lastScannedPlayer.IsDead)
				{
					m_lastScannedPlayer = null;
				}
				Player player = null;
				if (m_lastScannedPlayer != null && m_lastScannedPlayerStatus == StatusType.ClearLOS)
				{
					player = m_lastScannedPlayer;
					if (base.Owner.GameWorld.ElapsedTotalRealTime - m_lastScannedPlayerTimestamp < 1000f)
					{
						return;
					}
				}
				else if (player == null)
				{
					int num = base.Owner.GameWorld.Players.Count;
					while (num > 0 && player == null)
					{
						num--;
						m_lastScannedPlayerIndex = (m_lastScannedPlayerIndex + 1) % base.Owner.GameWorld.Players.Count;
						player = base.Owner.GameWorld.Players[m_lastScannedPlayerIndex];
						if (player == base.Owner || player.IsDisposed || player.IsDead || player.IsRemoved || player.InSameTeam(base.Owner))
						{
							player = null;
						}
					}
				}
				if (player != null)
				{
					m_lastScannedPlayerStatus = StatusType.Queued;
					m_lastScannedPlayer = player;
					m_lastScannedPlayerTimestamp = base.Owner.GameWorld.ElapsedTotalRealTime;
					LOSOpponent = player;
					PerformCheck();
				}
				else
				{
					m_lastScannedPlayerStatus = StatusType.None;
					LOSOpponent = null;
				}
			}
			else
			{
				ClearData();
			}
		}

		public void PerformCheck()
		{
			Player target = m_lastScannedPlayer;
			LOSOrigin = base.Owner.GetLOSWeaponOrigin(SFD.Weapons.WeaponItemType.NONE, out m_losTunnelingDistance);
			bool clearLOS = true;
			Microsoft.Xna.Framework.Vector2 preBox2DPosition = target.PreBox2DPosition;
			HashSet<ObjectData> cachedObjectsChecked = new HashSet<ObjectData>();
			PlayerAIPackageLOSCheck.RayCast(base.Owner, LOSOrigin, preBox2DPosition, startOverlapCheck: true, delegate(ObjectData od, Fixture fixture, float fraction)
			{
				if (od != base.Owner.ObjectData)
				{
					if (od == target.ObjectData)
					{
						return true;
					}
					if (fixture.ProjectileHit & fixture.AbsorbProjectile)
					{
						clearLOS = false;
						return true;
					}
				}
				return false;
			}, cachedObjectsChecked);
			m_lastScannedPlayerStatus = (clearLOS ? StatusType.ClearLOS : StatusType.Obstructed);
		}

		public override void ClearData()
		{
			LOSOpponent = null;
			m_lastScannedPlayer = null;
			m_lastScannedPlayerIndex = 0;
			m_lastScannedPlayerStatus = StatusType.None;
		}
	}

	public class PlayerAIPackageLOSCheck : PlayerAIPackage
	{
		public class WeaponLOSCheck
		{
			public int WeaponID;

			public float GravityArchingEffect;

			public int StatusCount;

			public bool IsHipFire;

			public FixedArray3B<LOSStatus> Statuses;

			public FixedArray3<Microsoft.Xna.Framework.Vector2> LOSTargetPoints;

			public Microsoft.Xna.Framework.Vector2 LOSOrigin;

			public float LOSTunnelingDistance;

			public List<Microsoft.Xna.Framework.Vector2> Path;

			public Microsoft.Xna.Framework.Vector2 TargetAim;

			public bool PathReached;

			public float PathTargetEndDistance;

			public Microsoft.Xna.Framework.Vector2 DEBUG_PathTargetEndPosition;

			public Microsoft.Xna.Framework.Vector2 DEBUG_PathTargetPosition;

			public float PathTargetAimExtra;

			public WeaponLOSCheck()
			{
				Statuses[0] = new LOSStatus();
				Statuses[1] = new LOSStatus();
				Statuses[2] = new LOSStatus();
				Path = new List<Microsoft.Xna.Framework.Vector2>(2);
			}

			public void UpdateTargetExtraAim(float gravityArchingEffect)
			{
				float num = (LOSTargetPoints[0] - LOSOrigin).CalcSafeLength();
				Microsoft.Xna.Framework.Vector2 vector = (DEBUG_PathTargetEndPosition = GetIntersectPointOnCircle(Path, num));
				DEBUG_PathTargetPosition = LOSTargetPoints[0];
				Microsoft.Xna.Framework.Vector2 x = LOSTargetPoints[0] - vector;
				float num2 = x.CalcSafeLength();
				PathTargetEndDistance = num;
				float num3 = ((x.Y != 0f) ? ((float)(-Math.Sign(x.Y))) : 1f);
				float num4 = (((LOSOrigin - vector).X > 0f) ? 1f : (-1f));
				float num5 = (float)Math.PI * 2f * num;
				float num6 = (float)Math.PI * 2f * (num2 / num5) * num3;
				if (Math.Abs(num6) > (float)Math.PI * 33f / 100f)
				{
					PathTargetAimExtra = 0f;
					PathReached = false;
				}
				else
				{
					PathTargetAimExtra += num6 * num4;
					PathReached = num2 < 0.79999995f;
				}
			}

			public Microsoft.Xna.Framework.Vector2 GetIntersectPointOnCircle(List<Microsoft.Xna.Framework.Vector2> path, float r)
			{
				if (path.Count <= 1)
				{
					return path[0];
				}
				Microsoft.Xna.Framework.Vector2 vector = path[0];
				Microsoft.Xna.Framework.Vector2 vector2 = path[1];
				float num = 0f;
				int num2 = 1;
				float num3;
				while (true)
				{
					if (num2 < path.Count)
					{
						vector2 = path[num2];
						num3 = (path[0] - vector2).CalcSafeLength();
						if (!(num3 < r))
						{
							break;
						}
						num = (path[0] - vector2).CalcSafeLength();
						vector = vector2;
						num2++;
						continue;
					}
					Microsoft.Xna.Framework.Vector2 vector3 = vector2 - path[0];
					vector3.Normalize();
					if (!vector3.IsValid())
					{
						return vector2;
					}
					vector2 = vector + vector3 * (r * 2f + 1f);
					float num4 = (path[0] - vector2).CalcSafeLength() - num;
					float num5 = (r - num) / num4;
					return vector + (vector2 - vector) * num5;
				}
				float num6 = num3 - num;
				float num7 = (r - num) / num6;
				return vector + (vector2 - vector) * num7;
			}

			public int GetPriorityLOSIndex()
			{
				int result = 0;
				if (StatusCount > 1)
				{
					if (Statuses[1].EndTargetReached && Statuses[1].TotalObjectHealth < Statuses[0].TotalObjectHealth)
					{
						result = 1;
					}
					if (Statuses[2].EndTargetReached && Statuses[2].TotalObjectHealth < Statuses[1].TotalObjectHealth && StatusCount > 2)
					{
						result = 2;
					}
				}
				return result;
			}

			public void Clear()
			{
				StatusCount = 0;
				Statuses[0].Clear();
				Statuses[1].Clear();
				Statuses[2].Clear();
				Path.Clear();
			}

			public void ClearStatus()
			{
				Statuses[0].Clear();
				Statuses[1].Clear();
				Statuses[2].Clear();
			}
		}

		public class LOSStatus
		{
			public enum ResultType
			{
				InSight,
				ObscuredByObjects,
				NoLOS
			}

			public int ObjectCheckCount;

			public bool UndestructableObject;

			public float MaxObjectStrength;

			public float MaxObjectHealth;

			public float TotalObjectStrength;

			public float TotalObjectHealth;

			public int AbsorbProjectileObjectCount;

			public bool BlockFire;

			public bool EndTargetReached;

			public float EndTargetDistance;

			public List<float> TeammatesDistances;

			public float ClosestObjectDistance;

			public int TeammatesCount => TeammatesDistances.Count;

			public ResultType GetResult(bool mustReachEndTarget)
			{
				if (UndestructableObject)
				{
					return ResultType.NoLOS;
				}
				if (mustReachEndTarget && !EndTargetReached)
				{
					return ResultType.NoLOS;
				}
				if (ObjectCheckCount > 0)
				{
					return ResultType.ObscuredByObjects;
				}
				return ResultType.InSight;
			}

			public Microsoft.Xna.Framework.Color GetDebugColor(bool mustReachEndTarget)
			{
				Microsoft.Xna.Framework.Color result = Microsoft.Xna.Framework.Color.Green;
				switch (GetResult(mustReachEndTarget))
				{
				case ResultType.NoLOS:
					result = Microsoft.Xna.Framework.Color.Red;
					break;
				case ResultType.ObscuredByObjects:
					result = Microsoft.Xna.Framework.Color.Yellow;
					break;
				}
				return result;
			}

			public LOSStatus()
			{
				TeammatesDistances = new List<float>();
				Clear();
			}

			public void Clear()
			{
				EndTargetReached = false;
				EndTargetDistance = 0f;
				ObjectCheckCount = 0;
				UndestructableObject = false;
				MaxObjectStrength = 0f;
				MaxObjectHealth = 0f;
				TotalObjectStrength = 0f;
				TotalObjectHealth = 0f;
				AbsorbProjectileObjectCount = 0;
				BlockFire = false;
				ClosestObjectDistance = -1f;
				TeammatesDistances.Clear();
			}
		}

		public ObjectData m_target;

		public float m_losTunnelingDistance;

		public WeaponLOSCheck m_primaryLOSCheck;

		public WeaponLOSCheck m_secondaryLOSCheck;

		public WeaponLOSCheck m_primaryLOSCheckHipFire;

		public WeaponLOSCheck m_secondaryLOSCheckHipFire;

		public float m_cacheTime;

		public const float MAX_CACHE_TIME = 50f;

		public bool m_hipFireStatusUpdated;

		public byte m_processStep;

		public ObjectData Target => m_target;

		public Microsoft.Xna.Framework.Vector2 LOSOrigin { get; set; }

		public float LOSTunnelingDistance
		{
			get
			{
				return m_losTunnelingDistance;
			}
			set
			{
				m_losTunnelingDistance = value;
			}
		}

		public WeaponLOSCheck PrimaryLOSCheck => m_primaryLOSCheck;

		public WeaponLOSCheck SecondaryLOSCheck
		{
			get
			{
				if (!PrimarySecondaryIsSameLOSGravityArcing)
				{
					return m_secondaryLOSCheck;
				}
				return m_primaryLOSCheck;
			}
		}

		public bool PrimarySecondaryIsSameLOSGravityArcing { get; set; }

		public WeaponLOSCheck PrimaryLOSCheckHipFire => m_primaryLOSCheckHipFire;

		public WeaponLOSCheck SecondaryLOSCheckHipFire
		{
			get
			{
				if (!PrimarySecondaryIsSameHipFireOffset)
				{
					return m_secondaryLOSCheckHipFire;
				}
				return m_primaryLOSCheckHipFire;
			}
		}

		public bool PrimarySecondaryIsSameHipFireOffset { get; set; }

		public bool CanUpdateHipFire { get; set; }

		public bool SetTarget(ObjectData value)
		{
			if (m_target != value)
			{
				m_target = value;
				m_primaryLOSCheck.ClearStatus();
				m_secondaryLOSCheck.ClearStatus();
				m_primaryLOSCheckHipFire.ClearStatus();
				m_secondaryLOSCheckHipFire.ClearStatus();
				return true;
			}
			return false;
		}

		public PlayerAIPackageLOSCheck()
		{
			m_primaryLOSCheck = new WeaponLOSCheck();
			m_secondaryLOSCheck = new WeaponLOSCheck();
			m_primaryLOSCheckHipFire = new WeaponLOSCheck();
			m_secondaryLOSCheckHipFire = new WeaponLOSCheck();
			m_primaryLOSCheckHipFire.IsHipFire = true;
			m_secondaryLOSCheckHipFire.IsHipFire = true;
		}

		public void UpdateTargetPoints(float ms, bool deepUpdate, bool updateHipFire)
		{
			m_cacheTime += ms;
			if (m_cacheTime < 50f && !deepUpdate)
			{
				return;
			}
			m_cacheTime = 0f;
			LOSOrigin = base.Owner.GetLOSWeaponOrigin(SFD.Weapons.WeaponItemType.Rifle, out m_losTunnelingDistance);
			FixedArray3<Microsoft.Xna.Framework.Vector2> fixedArray = default(FixedArray3<Microsoft.Xna.Framework.Vector2>);
			if (Target != null && !Target.IsDisposed)
			{
				if (Target.IsPlayer)
				{
					FixedArray2<Microsoft.Xna.Framework.Vector2> lOSTargetPoints = ((Player)Target.InternalData).GetLOSTargetPoints();
					fixedArray[0] = lOSTargetPoints[0] + (lOSTargetPoints[1] - lOSTargetPoints[0]) * 0.5f;
					Microsoft.Xna.Framework.Vector2 vector = fixedArray[0] - Microsoft.Xna.Framework.Vector2.UnitX * 0.128f;
					Microsoft.Xna.Framework.Vector2 vector2 = fixedArray[0] + Microsoft.Xna.Framework.Vector2.UnitX * 0.128f;
					Microsoft.Xna.Framework.Vector2 value = fixedArray[0] - LOSOrigin;
					value.Normalize();
					float num = Math.Abs(Microsoft.Xna.Framework.Vector2.Dot(Microsoft.Xna.Framework.Vector2.UnitY, value));
					if (((value.X < 0f) & (value.Y < 0f)) | ((value.X > 0f) & (value.Y > 0f)))
					{
						value = vector;
						vector = vector2;
						vector2 = value;
					}
					fixedArray[1] = lOSTargetPoints[0] * (1f - num) + vector * num;
					fixedArray[2] = lOSTargetPoints[1] * (1f - num) + vector2 * num;
					m_primaryLOSCheck.StatusCount = 3;
					m_secondaryLOSCheck.StatusCount = 3;
				}
				else
				{
					fixedArray[0] = Target.GetBox2DCenterPosition();
					fixedArray[1] = Microsoft.Xna.Framework.Vector2.Zero;
					fixedArray[2] = Microsoft.Xna.Framework.Vector2.Zero;
					m_primaryLOSCheck.StatusCount = 1;
					m_secondaryLOSCheck.StatusCount = 1;
					if (!(Target is ObjectStreetsweeper) && Target.Body != null && Target.Body.GetFixtureList() != null)
					{
						Box2D.XNA.RayCastInput input = new Box2D.XNA.RayCastInput
						{
							p1 = LOSOrigin,
							p2 = fixedArray[0],
							maxFraction = 1f
						};
						if (Target.Body.GetFixtureList().RayCast(out var output, ref input, startInsideCollision: false))
						{
							Microsoft.Xna.Framework.Vector2 hitPosition = input.GetHitPosition(output.fraction);
							Microsoft.Xna.Framework.Vector2 position = fixedArray[0] - hitPosition;
							float num2 = 35f;
							SFDMath.RotatePosition(ref position, 0.61086524f, out position);
							fixedArray[1] = fixedArray[0] + position * 0.7f;
							SFDMath.RotatePosition(ref position, -1.2217305f, out position);
							fixedArray[2] = fixedArray[0] + position * 0.7f;
							fixedArray[0] = hitPosition;
							m_primaryLOSCheck.StatusCount = 3;
							m_secondaryLOSCheck.StatusCount = 3;
							Microsoft.Xna.Framework.Vector2 position2 = input.p1 - input.p2;
							SFDMath.RotatePosition(ref position2, 0.61086524f, out position2);
							input.p1 = input.p2 + position2;
							if (Target.Body.GetFixtureList().RayCast(out output, ref input, startInsideCollision: true))
							{
								fixedArray[1] = input.GetHitPosition(output.fraction);
							}
							SFDMath.RotatePosition(ref position2, -(float)Math.PI / 180f * num2 * 2f, out position2);
							input.p1 = input.p2 + position2;
							if (Target.Body.GetFixtureList().RayCast(out output, ref input, startInsideCollision: true))
							{
								fixedArray[2] = input.GetHitPosition(output.fraction);
							}
						}
					}
				}
			}
			else
			{
				fixedArray[0] = Microsoft.Xna.Framework.Vector2.Zero;
				fixedArray[1] = Microsoft.Xna.Framework.Vector2.Zero;
				fixedArray[2] = Microsoft.Xna.Framework.Vector2.Zero;
				m_primaryLOSCheck.StatusCount = 1;
				m_secondaryLOSCheck.StatusCount = 1;
			}
			m_primaryLOSCheck.LOSTargetPoints[0] = fixedArray[0];
			m_primaryLOSCheck.LOSTargetPoints[1] = fixedArray[1];
			m_primaryLOSCheck.LOSTargetPoints[2] = fixedArray[2];
			m_primaryLOSCheck.LOSOrigin = LOSOrigin;
			m_primaryLOSCheck.LOSTunnelingDistance = LOSTunnelingDistance;
			m_primaryLOSCheck.TargetAim = GetTargetAim(base.Owner.CurrentRifleWeapon, m_primaryLOSCheck);
			m_primaryLOSCheck.WeaponID = ((base.Owner.CurrentRifleWeapon != null) ? base.Owner.CurrentRifleWeapon.Properties.WeaponID : 0);
			m_secondaryLOSCheck.LOSTargetPoints[0] = fixedArray[0];
			m_secondaryLOSCheck.LOSTargetPoints[1] = fixedArray[1];
			m_secondaryLOSCheck.LOSTargetPoints[2] = fixedArray[2];
			m_secondaryLOSCheck.LOSOrigin = base.Owner.GetLOSWeaponOrigin(SFD.Weapons.WeaponItemType.Handgun, out m_secondaryLOSCheck.LOSTunnelingDistance);
			m_secondaryLOSCheck.WeaponID = ((base.Owner.CurrentHandgunWeapon != null) ? base.Owner.CurrentHandgunWeapon.Properties.WeaponID : 0);
			if (PrimarySecondaryIsSameLOSGravityArcing)
			{
				m_secondaryLOSCheck.TargetAim = m_primaryLOSCheck.TargetAim;
				m_secondaryLOSCheck.PathTargetAimExtra = m_primaryLOSCheck.PathTargetAimExtra;
			}
			else
			{
				m_secondaryLOSCheck.TargetAim = GetTargetAim(base.Owner.CurrentHandgunWeapon, m_secondaryLOSCheck);
			}
			if (updateHipFire)
			{
				m_primaryLOSCheckHipFire.LOSOrigin = base.Owner.GetLOSWeaponOriginHipFire(SFD.Weapons.WeaponItemType.Rifle, out m_primaryLOSCheckHipFire.LOSTunnelingDistance);
				m_primaryLOSCheckHipFire.TargetAim = Microsoft.Xna.Framework.Vector2.UnitX * ((m_primaryLOSCheck.TargetAim.X > 0f) ? 1f : (-1f));
				m_primaryLOSCheckHipFire.StatusCount = 1;
				m_primaryLOSCheckHipFire.LOSTargetPoints[0] = new Microsoft.Xna.Framework.Vector2(fixedArray[0].X, m_primaryLOSCheckHipFire.LOSOrigin.Y);
				if (PrimarySecondaryIsSameHipFireOffset)
				{
					m_secondaryLOSCheckHipFire.LOSOrigin = m_primaryLOSCheckHipFire.LOSOrigin;
					m_secondaryLOSCheckHipFire.LOSTunnelingDistance = m_primaryLOSCheckHipFire.LOSTunnelingDistance;
				}
				else
				{
					m_secondaryLOSCheckHipFire.LOSOrigin = base.Owner.GetLOSWeaponOriginHipFire(SFD.Weapons.WeaponItemType.Handgun, out m_secondaryLOSCheckHipFire.LOSTunnelingDistance);
				}
				m_secondaryLOSCheckHipFire.TargetAim = m_primaryLOSCheckHipFire.TargetAim;
				m_secondaryLOSCheckHipFire.StatusCount = 1;
				m_secondaryLOSCheckHipFire.LOSTargetPoints[0] = new Microsoft.Xna.Framework.Vector2(fixedArray[0].X, m_secondaryLOSCheckHipFire.LOSOrigin.Y);
			}
			if (!deepUpdate)
			{
				return;
			}
			m_primaryLOSCheck.Path.Clear();
			m_secondaryLOSCheck.Path.Clear();
			RWeapon currentRifleWeapon = base.Owner.CurrentRifleWeapon;
			if (currentRifleWeapon != null && currentRifleWeapon.Properties.AI_GravityArcingEffect > 0f)
			{
				CalculatePathForArcingWeapon(currentRifleWeapon, m_primaryLOSCheck);
			}
			if (!PrimarySecondaryIsSameLOSGravityArcing)
			{
				RWeapon currentHandgunWeapon = base.Owner.CurrentHandgunWeapon;
				if (currentHandgunWeapon != null && currentHandgunWeapon.Properties.AI_GravityArcingEffect > 0f)
				{
					CalculatePathForArcingWeapon(currentHandgunWeapon, m_secondaryLOSCheck);
				}
			}
			else
			{
				m_secondaryLOSCheck.PathTargetAimExtra = m_primaryLOSCheck.PathTargetAimExtra;
			}
		}

		public void CalculatePathForArcingWeapon(RWeapon wpn, WeaponLOSCheck weaponLOSCheck)
		{
			weaponLOSCheck.Path.Clear();
			weaponLOSCheck.Path.Add(weaponLOSCheck.LOSOrigin);
			weaponLOSCheck.GravityArchingEffect = wpn.Properties.AI_GravityArcingEffect;
			float num = 500f;
			float num2 = 0f;
			float num3 = 0f;
			Microsoft.Xna.Framework.Vector2 vector = weaponLOSCheck.TargetAim;
			Microsoft.Xna.Framework.Vector2 vector2 = vector * ((wpn.Properties.Projectile != null) ? wpn.Properties.Projectile.Properties.InitialSpeed : 1000f);
			Microsoft.Xna.Framework.Vector2 vector3 = Converter.Box2DToWorld(weaponLOSCheck.LOSOrigin);
			int num4 = 0;
			Microsoft.Xna.Framework.Vector2 vector4 = Converter.Box2DToWorld(weaponLOSCheck.LOSTargetPoints[0]);
			Microsoft.Xna.Framework.Vector2 a = vector4 + Microsoft.Xna.Framework.Vector2.UnitY;
			Microsoft.Xna.Framework.Vector2 a2 = vector4;
			Microsoft.Xna.Framework.Vector2 a3 = vector4 + Microsoft.Xna.Framework.Vector2.UnitX;
			while (num2 < num)
			{
				float num5 = 1f;
				if (num3 < 500f)
				{
					num3 += 80f;
					num5 = num3 / 500f;
					num3 += 20f;
				}
				else
				{
					num3 += 100f;
				}
				Microsoft.Xna.Framework.Vector2 vector5 = vector2 * 0.1f;
				float num6 = vector5.CalcSafeLength();
				vector5.Normalize();
				Microsoft.Xna.Framework.Vector2 zero = Microsoft.Xna.Framework.Vector2.Zero;
				float num7 = Microsoft.Xna.Framework.Vector2.Distance(value2: (!(Math.Abs(Microsoft.Xna.Framework.Vector2.Dot(vector5, Microsoft.Xna.Framework.Vector2.UnitY)) < 0.77f)) ? SFDMath.LineLineIntersection(vector3, vector3 + vector5, a2, a3) : SFDMath.LineLineIntersection(vector3, vector3 + vector5, vector4, a), value1: vector3);
				if (num7 < num6)
				{
					num6 = num7;
					num2 = num;
				}
				vector3 += vector5 * num6;
				vector2.Y -= wpn.Properties.AI_GravityArcingEffect * num5 * 100f;
				num2 += num6;
				num5 = Microsoft.Xna.Framework.Vector2.Dot(vector, vector5);
				if (num5 < 0.99f)
				{
					weaponLOSCheck.Path.Add(Converter.WorldToBox2D(vector3));
					vector = vector5;
					num4 = 0;
				}
				else
				{
					num4++;
				}
			}
			if (num4 > 0)
			{
				weaponLOSCheck.Path.Add(Converter.WorldToBox2D(vector3));
			}
			weaponLOSCheck.UpdateTargetExtraAim(wpn.Properties.AI_GravityArcingEffect);
		}

		public Microsoft.Xna.Framework.Vector2 GetTargetAim(RWeapon wpn, WeaponLOSCheck weaponLOSCheck)
		{
			Microsoft.Xna.Framework.Vector2 zero = Microsoft.Xna.Framework.Vector2.Zero;
			if (wpn != null && wpn.Properties.AI_GravityArcingEffect > 0f)
			{
				zero = weaponLOSCheck.LOSTargetPoints[0] - weaponLOSCheck.LOSOrigin;
				Math.Abs(zero.X);
				zero.Normalize();
				SFDMath.RotatePosition(ref zero, weaponLOSCheck.PathTargetAimExtra, out zero);
			}
			else
			{
				int priorityLOSIndex = weaponLOSCheck.GetPriorityLOSIndex();
				zero = weaponLOSCheck.LOSTargetPoints[priorityLOSIndex] - weaponLOSCheck.LOSOrigin;
				if (zero.CalcSafeLengthSquared() < 0.04f)
				{
					zero = new Microsoft.Xna.Framework.Vector2(base.Owner.LastDirectionX, 0f);
				}
				else
				{
					zero.Normalize();
				}
			}
			float num = (float)Math.Atan2(zero.Y, zero.X);
			return new Microsoft.Xna.Framework.Vector2((float)Math.Cos(num), (float)Math.Sin(num));
		}

		public override void Process()
		{
			if (m_processStep == 0)
			{
				if (base.Owner.CurrentRifleWeapon != null && base.Owner.CurrentHandgunWeapon != null)
				{
					PrimarySecondaryIsSameLOSGravityArcing = base.Owner.CurrentRifleWeapon.Properties.AI_GravityArcingEffect == base.Owner.CurrentHandgunWeapon.Properties.AI_GravityArcingEffect;
					PrimarySecondaryIsSameHipFireOffset = base.Owner.CurrentRifleWeapon.Visuals.HipFireWeaponOffset == base.Owner.CurrentHandgunWeapon.Visuals.HipFireWeaponOffset;
				}
				else
				{
					PrimarySecondaryIsSameLOSGravityArcing = false;
					PrimarySecondaryIsSameHipFireOffset = false;
				}
				m_primaryLOSCheck.Clear();
				m_secondaryLOSCheck.Clear();
				if (Target != null && !Target.IsDisposed)
				{
					CanUpdateHipFire = Math.Abs((Target.GetBox2DPosition() - base.Owner.PreBox2DPosition).Y) < 1.04f;
				}
				else
				{
					CanUpdateHipFire = false;
				}
				UpdateTargetPoints(0f, deepUpdate: true, CanUpdateHipFire);
				if (base.Owner.CurrentRifleWeapon != null)
				{
					ProcessWeaponLOSCheck(m_primaryLOSCheck);
					m_primaryLOSCheck.TargetAim = GetTargetAim(base.Owner.CurrentRifleWeapon, m_primaryLOSCheck);
					if (CanUpdateHipFire && base.Owner.CurrentRifleWeapon.Properties.AI_GravityArcingEffect == 0f)
					{
						m_processStep = 1;
					}
				}
				if (base.Owner.CurrentHandgunWeapon != null)
				{
					if (!PrimarySecondaryIsSameLOSGravityArcing)
					{
						ProcessWeaponLOSCheck(m_secondaryLOSCheck);
						m_secondaryLOSCheck.TargetAim = GetTargetAim(base.Owner.CurrentHandgunWeapon, m_secondaryLOSCheck);
					}
					if (CanUpdateHipFire && base.Owner.CurrentHandgunWeapon.Properties.AI_GravityArcingEffect == 0f)
					{
						m_processStep = 1;
					}
				}
				if (m_processStep == 1)
				{
					base.ForceAnotherProcessPass = true;
				}
				else if (m_hipFireStatusUpdated)
				{
					m_primaryLOSCheckHipFire.ClearStatus();
					m_secondaryLOSCheckHipFire.ClearStatus();
					m_hipFireStatusUpdated = false;
				}
			}
			else
			{
				m_processStep = 0;
				m_primaryLOSCheckHipFire.ClearStatus();
				m_secondaryLOSCheckHipFire.ClearStatus();
				m_hipFireStatusUpdated = true;
				if (base.Owner.CurrentRifleWeapon != null && base.Owner.CurrentRifleWeapon.Properties.AI_GravityArcingEffect == 0f)
				{
					ProcessWeaponLOSCheck(m_primaryLOSCheckHipFire);
					m_primaryLOSCheck.TargetAim = GetTargetAim(base.Owner.CurrentRifleWeapon, m_primaryLOSCheck);
				}
				if (!PrimarySecondaryIsSameHipFireOffset && base.Owner.CurrentHandgunWeapon != null && base.Owner.CurrentHandgunWeapon.Properties.AI_GravityArcingEffect == 0f)
				{
					ProcessWeaponLOSCheck(m_secondaryLOSCheckHipFire);
					m_secondaryLOSCheck.TargetAim = GetTargetAim(base.Owner.CurrentHandgunWeapon, m_secondaryLOSCheck);
				}
			}
		}

		public void ProcessWeaponLOSCheck(WeaponLOSCheck weaponLOSCheck)
		{
			if (weaponLOSCheck.Path.Count > 1)
			{
				weaponLOSCheck.StatusCount = 1;
				if (!weaponLOSCheck.PathReached)
				{
					LOSStatus lOSStatus = weaponLOSCheck.Statuses[0];
					lOSStatus.EndTargetReached = false;
					lOSStatus.UndestructableObject = true;
					return;
				}
				LOSStatus losStatus = weaponLOSCheck.Statuses[0];
				HashSet<ObjectData> cachedObjectsChecked = new HashSet<ObjectData>();
				bool terminate = false;
				float baseDistance = 0f;
				for (int i = 1; i < weaponLOSCheck.Path.Count; i++)
				{
					Box2D.XNA.RayCastInput rci = default(Box2D.XNA.RayCastInput);
					rci.p1 = weaponLOSCheck.Path[i - 1];
					rci.p2 = weaponLOSCheck.Path[i];
					rci.maxFraction = 1f;
					RayCast(base.Owner, rci.p1, rci.p2, i == 1, delegate(ObjectData od, Fixture fixture, float fraction)
					{
						if (od != base.Owner.ObjectData && RayCastObjectCheckUpdateLOSStatus(od, fixture, weaponLOSCheck, losStatus, (bool exitPoint) => exitPoint ? (baseDistance + Microsoft.Xna.Framework.Vector2.Distance(rci.p1, RayCastObjectGetExitPoint(od, fixture, ref rci))) : (baseDistance + rci.GetHitDistance(fraction))))
						{
							terminate = true;
							return true;
						}
						return false;
					}, cachedObjectsChecked);
					losStatus.EndTargetReached |= !losStatus.UndestructableObject;
					if (!terminate)
					{
						baseDistance += (rci.p2 - rci.p1).CalcSafeLength();
						continue;
					}
					break;
				}
				return;
			}
			if (weaponLOSCheck.IsHipFire)
			{
				if (Target != null && !Target.IsDisposed && Math.Abs((Target.GetBox2DPosition() - base.Owner.PreBox2DPosition).Y) > 0.79999995f)
				{
					return;
				}
				weaponLOSCheck.StatusCount = 1;
			}
			int num = 0;
			while (true)
			{
				if (num >= weaponLOSCheck.StatusCount)
				{
					return;
				}
				Box2D.XNA.RayCastInput rci2 = default(Box2D.XNA.RayCastInput);
				rci2.p1 = weaponLOSCheck.LOSOrigin;
				rci2.p2 = weaponLOSCheck.LOSTargetPoints[num];
				rci2.maxFraction = 1f;
				LOSStatus losStatus2 = weaponLOSCheck.Statuses[num];
				losStatus2.ClosestObjectDistance = -1f;
				if (Microsoft.Xna.Framework.Vector2.DistanceSquared(rci2.p1, rci2.p2) < 0.04f)
				{
					losStatus2.EndTargetReached = true;
				}
				else
				{
					RayCast(base.Owner, rci2.p1, rci2.p2, startOverlapCheck: true, (ObjectData od, Fixture fixture, float fraction) => od != base.Owner.ObjectData && RayCastObjectCheckUpdateLOSStatus(od, fixture, weaponLOSCheck, losStatus2, (bool exitPoint) => exitPoint ? Microsoft.Xna.Framework.Vector2.Distance(rci2.p1, RayCastObjectGetExitPoint(od, fixture, ref rci2)) : rci2.GetHitDistance(fraction)));
					losStatus2.EndTargetReached |= !losStatus2.UndestructableObject && !weaponLOSCheck.IsHipFire;
				}
				if (Target == null || (num == 0 && losStatus2.ObjectCheckCount == 0 && losStatus2.EndTargetReached))
				{
					break;
				}
				num++;
			}
			weaponLOSCheck.StatusCount = 1;
		}

		public Microsoft.Xna.Framework.Vector2 RayCastObjectGetExitPoint(ObjectData od, Fixture fixture, ref Box2D.XNA.RayCastInput rci)
		{
			Box2D.XNA.RayCastInput input = new Box2D.XNA.RayCastInput
			{
				p1 = rci.GetHitPosition(1f),
				p2 = rci.p1,
				maxFraction = 1f
			};
			RayCastOutput output;
			if (od.IsPlayer)
			{
				((Player)od.InternalData).GetAABBWhole(out var aabb);
				if (aabb.RayCast(out output, ref input))
				{
					return input.GetHitPosition(output.fraction);
				}
			}
			else if (fixture.RayCast(out output, ref input))
			{
				return input.GetHitPosition(output.fraction);
			}
			return input.p1;
		}

		public bool RayCastObjectCheckUpdateLOSStatus(ObjectData od, Fixture fixture, WeaponLOSCheck weaponLOSCheck, LOSStatus losStatus, Func<bool, float> fHitDistance)
		{
			bool flag;
			float num = (((flag = fixture.ProjectileHit && !fixture.IsCloud()) || od == Target) ? fHitDistance(arg: false) : 0f);
			if (od == Target)
			{
				losStatus.EndTargetReached = true;
				losStatus.EndTargetDistance = num;
				for (int num2 = losStatus.TeammatesDistances.Count - 1; num2 >= 0; num2--)
				{
					if (losStatus.TeammatesDistances[num2] > num)
					{
						losStatus.TeammatesDistances.RemoveAt(num2);
					}
				}
			}
			if (od.IsPlayer)
			{
				Player player = (Player)od.InternalData;
				if (base.Owner.InSameTeam(player))
				{
					if (num > weaponLOSCheck.LOSTunnelingDistance)
					{
						if (!losStatus.EndTargetReached || num < losStatus.EndTargetDistance)
						{
							losStatus.TeammatesDistances.Add(num);
						}
					}
					else
					{
						float num3 = fHitDistance(arg: true);
						if (num3 > weaponLOSCheck.LOSTunnelingDistance - 0.04f)
						{
							if (!losStatus.EndTargetReached || num3 < losStatus.EndTargetDistance)
							{
								losStatus.TeammatesDistances.Add(num3);
							}
						}
						else
						{
							flag = false;
						}
					}
				}
				else if (player.IsDead && player.LayingOnGround)
				{
					num = Math.Max(0f, num - 0.24f);
				}
				if (flag)
				{
					losStatus.ClosestObjectDistance = ((losStatus.ClosestObjectDistance == -1f) ? num : Math.Min(num, losStatus.ClosestObjectDistance));
				}
				return false;
			}
			if (fixture.IsCloud())
			{
				return false;
			}
			if (flag)
			{
				losStatus.ClosestObjectDistance = ((losStatus.ClosestObjectDistance == -1f) ? num : Math.Min(num, losStatus.ClosestObjectDistance));
				losStatus.BlockFire |= fixture.BlockFire;
				if (fixture.AbsorbProjectile)
				{
					losStatus.AbsorbProjectileObjectCount++;
				}
				losStatus.MaxObjectStrength = Math.Max(fixture.ObjectStrength, losStatus.MaxObjectStrength);
				losStatus.MaxObjectHealth = Math.Max(od.Health.CurrentValue, losStatus.MaxObjectHealth);
				losStatus.TotalObjectStrength += fixture.ObjectStrength;
				losStatus.TotalObjectHealth += od.Health.CurrentValue;
				losStatus.UndestructableObject |= !od.Destructable;
				losStatus.ObjectCheckCount++;
			}
			if (fixture.ProjectileHit && fixture.AbsorbProjectile)
			{
				return !od.Destructable;
			}
			return false;
		}

		public static void RayCast(Player ownerPlayer, Microsoft.Xna.Framework.Vector2 startPosition, Microsoft.Xna.Framework.Vector2 endPosition, bool startOverlapCheck, Func<ObjectData, Fixture, float, bool> objectCheck, HashSet<ObjectData> cachedObjectsChecked = null)
		{
			RayCastResult rayCastResult = default(RayCastResult);
			rayCastResult.StartPosition = startPosition;
			rayCastResult.EndPosition = endPosition;
			rayCastResult.EndFixture = null;
			Box2D.XNA.RayCastInput rci = default(Box2D.XNA.RayCastInput);
			RayCastOutput rco = default(RayCastOutput);
			rci.p1 = startPosition;
			rci.p2 = endPosition;
			rci.maxFraction = 1f;
			if (cachedObjectsChecked == null)
			{
				cachedObjectsChecked = new HashSet<ObjectData>();
			}
			cachedObjectsChecked.Add(ownerPlayer.ObjectData);
			bool terminated = false;
			if (startOverlapCheck)
			{
				AABB.Create(out var aabb, startPosition, 0.04f);
				ownerPlayer.GameWorld.GetActiveWorld.QueryAABB(delegate(Fixture fixture)
				{
					if (fixture != null && fixture.GetUserData() != null && !fixture.IsSensor())
					{
						ObjectData objectData = ObjectData.Read(fixture);
						if (!cachedObjectsChecked.Contains(objectData))
						{
							if (objectData.IsPlayer && !objectData.IsDisposed)
							{
								((Player)objectData.InternalData).GetAABBWhole(out var aabb3);
								if (aabb3.Contains(ref startPosition))
								{
									if (objectCheck(objectData, fixture, 0f))
									{
										terminated = true;
										cachedObjectsChecked.Add(objectData);
										return false;
									}
								}
								else if (aabb3.RayCast(out rco, ref rci) && objectCheck(objectData, fixture, rco.fraction))
								{
									terminated = true;
									cachedObjectsChecked.Add(objectData);
									return false;
								}
							}
							else if (fixture.TestPoint(startPosition) && objectCheck(objectData, fixture, 0f))
							{
								terminated = true;
								cachedObjectsChecked.Add(objectData);
								return false;
							}
						}
					}
					return true;
				}, ref aabb);
			}
			if (terminated)
			{
				return;
			}
			ownerPlayer.GameWorld.GetActiveWorld.RayCast(delegate(Fixture fixture, Microsoft.Xna.Framework.Vector2 point, Microsoft.Xna.Framework.Vector2 normal, float fraction)
			{
				if (fixture != null && fixture.GetUserData() != null && !fixture.IsSensor())
				{
					ObjectData objectData = ObjectData.Read(fixture);
					if (!cachedObjectsChecked.Contains(objectData))
					{
						if (objectData.IsPlayer && !objectData.IsDisposed)
						{
							Player player2 = (Player)objectData.InternalData;
							player2.GetAABBWhole(out var aabb3);
							if (player2.LayingOnGround && !player2.IsDead)
							{
								aabb3.upperBound.Y += 0.19999999f;
							}
							if (startOverlapCheck && aabb3.Contains(ref startPosition))
							{
								if (objectCheck(objectData, fixture, 0f))
								{
									cachedObjectsChecked.Add(objectData);
									return fraction;
								}
							}
							else if (aabb3.RayCast(out rco, ref rci) && objectCheck(objectData, fixture, rco.fraction))
							{
								cachedObjectsChecked.Add(objectData);
								return fraction;
							}
						}
						else if (objectCheck(objectData, fixture, fraction))
						{
							cachedObjectsChecked.Add(objectData);
							return fraction;
						}
					}
				}
				return 1f;
			}, startPosition, endPosition);
			for (int num = 0; num < ownerPlayer.GameWorld.Players.Count; num++)
			{
				Player player = ownerPlayer.GameWorld.Players[num];
				if (cachedObjectsChecked.Add(player.ObjectData))
				{
					player.GetAABBWhole(out var aabb2);
					if (aabb2.RayCast(out rco, ref rci) && objectCheck(player.ObjectData, player.GetFixtureCircle(), rco.fraction))
					{
						break;
					}
				}
			}
		}

		public override void ClearData()
		{
			m_target = null;
		}
	}

	public class PlayerAIPackageGuardPosition : PlayerAIPackage
	{
		public ObjectData m_currentGuardTarget;

		public ObjectData m_lastSourceTarget;

		public float m_lastSourceRandomPositionTime;

		public float m_guardPositionTime;

		public ObjectData GuardTarget
		{
			get
			{
				return m_currentGuardTarget;
			}
			set
			{
				if (m_currentGuardTarget != value)
				{
					m_currentGuardTarget = value;
					GuardPosition = null;
				}
			}
		}

		public ObjectData GuardPosition { get; set; }

		public bool IsOld
		{
			get
			{
				if (base.Owner != null && !base.Owner.IsDisposed)
				{
					return base.Owner.GameWorld.ElapsedTotalGameTime - m_lastSourceRandomPositionTime > 800f;
				}
				return false;
			}
		}

		public override void Process()
		{
			if (base.Owner != null && !base.Owner.IsDisposed)
			{
				if (GuardTarget != null && !GuardTarget.IsDisposed)
				{
					if (GuardPosition != null && GuardPosition.IsDisposed)
					{
						GuardPosition = null;
					}
					bool flag = m_lastSourceTarget != GuardTarget || base.GameWorld.ElapsedTotalGameTime - m_lastSourceRandomPositionTime > m_guardPositionTime;
					ObjectData objectData = ((GuardPosition != null) ? GuardPosition : base.Owner.ObjectData);
					if ((GuardTarget.GetBox2DCenterPosition() - objectData.GetBox2DCenterPosition()).CalcSafeLength() > Converter.WorldToBox2D(base.Owner.BotBehaviorSet.GuardRange))
					{
						flag = true;
					}
					bool guardBehind = false;
					Player guardPlayer = null;
					if (GuardTarget.IsPlayer)
					{
						guardPlayer = (Player)GuardTarget.InternalData;
						if ((guardPlayer.CurrentWeaponDrawn == SFD.Weapons.WeaponItemType.Handgun || guardPlayer.CurrentWeaponDrawn == SFD.Weapons.WeaponItemType.Rifle) && !guardPlayer.InThrowingMode && !guardPlayer.GetCurrentRangedWeaponInUse().IsEmpty)
						{
							guardBehind = true;
						}
						else if (guardPlayer.CurrentWeaponDrawn == SFD.Weapons.WeaponItemType.Thrown && !guardPlayer.InThrowingMode)
						{
							guardBehind = true;
						}
					}
					if (guardBehind && guardPlayer != null && GuardPosition != null && Math.Sign((GuardPosition.GetBox2DCenterPosition() - guardPlayer.PreBox2DPosition).X) == guardPlayer.LastDirectionX && m_guardPositionTime > 100f)
					{
						m_guardPositionTime = 100f;
					}
					m_lastSourceTarget = GuardTarget;
					if (flag)
					{
						m_guardPositionTime = Constants.RANDOM.NextFloat(3000f, 6000f);
						m_lastSourceRandomPositionTime = base.GameWorld.ElapsedTotalGameTime;
						SFD.PathGrid.PathGrid.CrawlPathValidConnection crawlFunction = (PathNode pathNodeA, PathNode pathNodeB, PathNodeConnection connection) => connection.Direction == PathNodeConnectionDirection.TwoWay && connection.CheckConnectionEnabled() && ((!guardBehind || guardPlayer == null || Math.Sign((pathNodeB.Box2DPosition - guardPlayer.PreBox2DPosition).X) != guardPlayer.LastDirectionX) ? true : false);
						PathNode startNode = base.Owner.GameWorld.PathGrid.FindClosestPathNodeBelow(GuardTarget.GetBox2DCenterPosition(), mustHaveOutgoingConnections: true);
						PathNode pathNode = base.Owner.GameWorld.PathGrid.Crawl(startNode, base.Owner.BotBehaviorSet.GuardRange, crawlFunction);
						GuardPosition = ((pathNode != null) ? pathNode.OwnerObject : GuardTarget);
					}
				}
				else
				{
					GuardPosition = null;
				}
			}
			else
			{
				ClearData();
			}
		}

		public override void ClearData()
		{
			GuardTarget = null;
			GuardPosition = null;
			m_lastSourceTarget = null;
		}
	}

	public class PlayerAIPackagePathFinding : PlayerAIPackage
	{
		public ObjectActivateTrigger m_scanningActivatorTarget;

		public SimpleLinkedList<ListPathPointNode> m_scanningActivatorResult;

		public int m_scanningActivatorResultSteps;

		public PathNodeConnection m_connectionWithActivators;

		public int m_connectionWithActivatorsInstanceID;

		public List<ObjectActivateTrigger> m_possibleActivatorObjects;

		public Dictionary<int, float> m_currentChokePointValues;

		public int GoalTargetType
		{
			get
			{
				if (HasConnectionWithActivatorsNotYetEnabled && ActivatorTarget != null)
				{
					return 1;
				}
				return 0;
			}
		}

		public ObjectData GoalTarget
		{
			get
			{
				if (HasConnectionWithActivatorsNotYetEnabled && ActivatorTarget != null)
				{
					return ActivatorTarget;
				}
				return Target;
			}
		}

		public SimpleLinkedList<ListPathPointNode> GoalResult
		{
			get
			{
				if (HasConnectionWithActivatorsNotYetEnabled && ActivatorResult != null)
				{
					return ActivatorResult;
				}
				return Result;
			}
		}

		public int GoalResultSteps
		{
			get
			{
				if (HasConnectionWithActivatorsNotYetEnabled)
				{
					return ActivatorResultSteps;
				}
				return ResultSteps;
			}
		}

		public bool HasConnectionWithActivatorsNotYetEnabled
		{
			get
			{
				if (ScanMode >= 1 && m_connectionWithActivators != null)
				{
					return (m_connectionWithActivatorsInstanceID == m_connectionWithActivators.InstanceID) & !m_connectionWithActivators.Enabled & m_connectionWithActivators.HasActivators;
				}
				return false;
			}
		}

		public ObjectData Target { get; set; }

		public SimpleLinkedList<ListPathPointNode> Result { get; set; }

		public int ResultSteps { get; set; }

		public int PathBasedOnTargetID { get; set; }

		public ObjectActivateTrigger ActivatorTarget { get; set; }

		public SimpleLinkedList<ListPathPointNode> ActivatorResult { get; set; }

		public int ActivatorResultSteps { get; set; }

		public bool HasActivator { get; set; }

		public int ScanMode { get; set; }

		public override void Process()
		{
			if (base.Owner != null && !base.Owner.IsDisposed && Target != null && !Target.IsDisposed)
			{
				if (m_currentChokePointValues == null)
				{
					m_currentChokePointValues = new Dictionary<int, float>();
				}
				Microsoft.Xna.Framework.Vector2 box2DPosition = base.Owner.ObjectData.GetBox2DPosition();
				PathNode pathNode;
				if (!base.Owner.StandingOnGround && base.Owner.BotAINav.NodeA != null && base.Owner.BotAINav.NodeA.NavType == PathNode.PathNodeNavType.LedgeGrab && Microsoft.Xna.Framework.Vector2.Distance(box2DPosition, base.Owner.BotAINav.NodeA.Box2DPosition) < 0.64f)
				{
					pathNode = base.Owner.BotAINav.NodeA;
				}
				else
				{
					float scanHeight = (base.Owner.StandingOnGround ? 0.39999998f : 0.16f);
					pathNode = base.GameWorld.PathGrid.FindClosestPathNodeBelow(box2DPosition, mustHaveOutgoingConnections: true, 1.1999999f, 2.56f, scanHeight);
				}
				PathNode pathNode2;
				if (ScanMode == 1)
				{
					if (m_possibleActivatorObjects != null && m_possibleActivatorObjects.Count != 0)
					{
						ObjectActivateTrigger objectActivateTrigger = m_possibleActivatorObjects[0];
						m_possibleActivatorObjects.RemoveAt(0);
						if (!objectActivateTrigger.IsDisposed)
						{
							pathNode2 = ((pathNode != null) ? base.GameWorld.PathGrid.FindClosestPathNodeBelow(objectActivateTrigger.GetBox2DCenterPosition(), mustHaveOutgoingConnections: false) : null);
							SimpleLinkedList<ListPathPointNode> scanningActivatorResult = null;
							int pathSteps = 0;
							PathNode solutionEndNode = null;
							if (pathNode2 != null)
							{
								scanningActivatorResult = base.GameWorld.PathGrid.FindPath(pathNode, pathNode2, out pathSteps, out solutionEndNode, delegate(PathNode pathNodeA, PathNode pathNodeB, PathNodeConnection connection)
								{
									if (!pathNodeA.Enabled | !pathNodeB.Enabled | !connection.Enabled | connection.BlockedByZone | pathNodeA.BlockedByZone | pathNodeB.BlockedByZone)
									{
										return float.NaN;
									}
									if (connection.ConnectionType == PathNodeConnectionType.Portal)
									{
										return 0f;
									}
									Microsoft.Xna.Framework.Vector2 vector = pathNodeA.Box2DPosition - pathNodeB.Box2DPosition;
									float num = vector.CalcSafeLength();
									if (pathNodeB.BlockedUpwards && num > 0.001f && Microsoft.Xna.Framework.Vector2.Dot(-Microsoft.Xna.Framework.Vector2.Normalize(vector), Microsoft.Xna.Framework.Vector2.UnitY) > 0.74f)
									{
										return num + 350f;
									}
									if (pathNodeB.NavType == PathNode.PathNodeNavType.Ladder)
									{
										num = ((!(pathNodeB.Box2DPosition.Y > pathNodeA.Box2DPosition.Y)) ? (num * 0.8f) : (num * 1.2f));
									}
									else if (pathNodeA.NavType == PathNode.PathNodeNavType.LedgeGrab || pathNodeB.NavType == PathNode.PathNodeNavType.LedgeGrab)
									{
										num *= 1.2f;
									}
									if (connection.ConnectionType == PathNodeConnectionType.Dive)
									{
										num *= 0.7f;
									}
									else if (connection.ConnectionType == PathNodeConnectionType.Jump)
									{
										if (!pathNodeA.IsElevatorNode && !pathNodeB.IsElevatorNode)
										{
											bool precise = (pathNodeA.FollowObjectID == 0) & (pathNodeB.FollowObjectID == 0);
											float num2 = 0.04f * base.Owner.GetMaxSprintJumpDistance(precise);
											if (pathNodeB.NavType == PathNode.PathNodeNavType.LedgeGrab)
											{
												num2 += 0.48f;
											}
											if (num > num2)
											{
												return float.NaN;
											}
										}
										num *= 1.2f;
									}
									return num * connection.CostMultiplier;
								});
							}
							if (pathNode2 == solutionEndNode && (m_scanningActivatorResult == null || pathSteps < m_scanningActivatorResultSteps))
							{
								if (m_scanningActivatorResult != null)
								{
									m_scanningActivatorResult.Free();
								}
								m_scanningActivatorTarget = objectActivateTrigger;
								m_scanningActivatorResult = scanningActivatorResult;
								m_scanningActivatorResultSteps = pathSteps;
							}
						}
						if (m_possibleActivatorObjects.Count == 0)
						{
							ScanMode = 2;
							if (m_scanningActivatorTarget == null)
							{
								base.Owner.m_botAIUnavailableActivateablePathConnections.Add(m_connectionWithActivators.InstanceID);
								ClearActivatorPathData();
							}
							else
							{
								if (ActivatorResult != null && ActivatorResult != m_scanningActivatorResult)
								{
									ActivatorResult.Free();
								}
								ActivatorResult = m_scanningActivatorResult;
								ActivatorResultSteps = m_scanningActivatorResultSteps;
								ActivatorTarget = m_scanningActivatorTarget;
								m_scanningActivatorResult = null;
								m_scanningActivatorTarget = null;
							}
						}
						if (ScanMode == 1)
						{
							base.ForceAnotherProcessPass = true;
						}
					}
					else
					{
						ScanMode = 2;
					}
					return;
				}
				ScanMode = 0;
				PathBasedOnTargetID = Target.ObjectID;
				pathNode2 = ((pathNode != null) ? base.GameWorld.PathGrid.FindClosestPathNodeBelow(Target.GetBox2DCenterPosition(), mustHaveOutgoingConnections: false) : null);
				SimpleLinkedList<ListPathPointNode> simpleLinkedList = null;
				if (Result != null)
				{
					Result.Free();
					Result = null;
				}
				ClearActivatorScanPathData();
				bool checkUnavailableActivateablePathConnections = base.Owner.m_botAIUnavailableActivateablePathConnections.Count > 0;
				int aliveTeamPlayerCount = -1;
				int pathSteps2 = 0;
				PathNode solutionEndNode2 = null;
				if (pathNode2 != null)
				{
					simpleLinkedList = base.GameWorld.PathGrid.FindPath(pathNode, pathNode2, out pathSteps2, out solutionEndNode2, delegate(PathNode pathNodeA, PathNode pathNodeB, PathNodeConnection connection)
					{
						if (!pathNodeA.Enabled | !pathNodeB.Enabled | (!connection.Enabled & !connection.HasActivators) | connection.BlockedByZone | pathNodeA.BlockedByZone | pathNodeB.BlockedByZone)
						{
							return float.NaN;
						}
						if (checkUnavailableActivateablePathConnections)
						{
							if (connection.Enabled)
							{
								base.Owner.m_botAIUnavailableActivateablePathConnections.Remove(connection.InstanceID);
							}
							else if (base.Owner.m_botAIUnavailableActivateablePathConnections.Contains(connection.InstanceID))
							{
								if (connection.HasActivators)
								{
									return 999999f;
								}
								return float.NaN;
							}
						}
						if (connection.ConnectionType == PathNodeConnectionType.Portal)
						{
							return 0f;
						}
						Microsoft.Xna.Framework.Vector2 vector = pathNodeA.Box2DPosition - pathNodeB.Box2DPosition;
						float num = vector.CalcSafeLength();
						if (pathNodeB.BlockedUpwards && num > 0.001f)
						{
							Microsoft.Xna.Framework.Vector2 value = -Microsoft.Xna.Framework.Vector2.Normalize(vector);
							if (Microsoft.Xna.Framework.Vector2.Dot(value, Microsoft.Xna.Framework.Vector2.UnitY) > 0.74f)
							{
								return num + 350f;
							}
							value = Microsoft.Xna.Framework.Vector2.Normalize(pathNodeB.Box2DPosition - base.Owner.PreBox2DPosition);
							if (value.IsValid() && Microsoft.Xna.Framework.Vector2.Dot(value, Microsoft.Xna.Framework.Vector2.UnitY) > 0.74f)
							{
								return num + 350f;
							}
						}
						if (pathNodeB.NavType == PathNode.PathNodeNavType.Ladder)
						{
							num = ((!(pathNodeB.Box2DPosition.Y > pathNodeA.Box2DPosition.Y)) ? (num * 0.8f) : (num * 1.2f));
						}
						else if (pathNodeA.NavType == PathNode.PathNodeNavType.LedgeGrab || pathNodeB.NavType == PathNode.PathNodeNavType.LedgeGrab)
						{
							num *= 1.2f;
						}
						if (connection.ConnectionType == PathNodeConnectionType.Dive)
						{
							num *= 0.7f;
						}
						else if (connection.ConnectionType == PathNodeConnectionType.Jump)
						{
							if (!pathNodeA.IsElevatorNode && !pathNodeB.IsElevatorNode)
							{
								bool precise = (pathNodeA.FollowObjectID == 0) & (pathNodeB.FollowObjectID == 0);
								float num2 = 0.04f * base.Owner.GetMaxSprintJumpDistance(precise);
								if (pathNodeB.NavType == PathNode.PathNodeNavType.LedgeGrab)
								{
									num2 += 0.48f;
								}
								if (num > num2)
								{
									return float.NaN;
								}
							}
							num *= 1.2f;
						}
						if (pathNodeB.ChokePoint)
						{
							if (aliveTeamPlayerCount == -1)
							{
								aliveTeamPlayerCount = ((base.Owner.CurrentTeam != Team.Independent) ? base.GameWorld.Players.Count((Player x) => x.CurrentTeam == base.Owner.CurrentTeam && !x.IsDead) : 0);
							}
							if (base.Owner.BotBehaviorSet.ChokePointPlayerCountThreshold != 0 && aliveTeamPlayerCount > base.Owner.BotBehaviorSet.ChokePointPlayerCountThreshold)
							{
								float value2 = 0f;
								if (!m_currentChokePointValues.TryGetValue(pathNodeB.InstanceID, out value2) && base.Owner.CurrentTeam != Team.Independent)
								{
									value2 += pathNodeB.GetChokePointValue((int)base.Owner.CurrentTeam);
								}
								num += Math.Min(value2, 320f);
							}
						}
						return num * connection.CostMultiplier;
					});
					StoreChokePointValues(simpleLinkedList);
				}
				ResultSteps = pathSteps2;
				Result = simpleLinkedList;
				m_connectionWithActivators = null;
				simpleLinkedList?.IterateItemNodes(delegate(ListPathPointNode x)
				{
					if (x.ConnectionToNext != null && !x.ConnectionToNext.Enabled && x.ConnectionToNext.HasActivators)
					{
						m_possibleActivatorObjects = x.ConnectionToNext.OwnerObject.GetEnableWithActivatorObjects();
						ScanMode = 1;
						HasActivator = true;
						m_connectionWithActivators = x.ConnectionToNext;
						m_connectionWithActivatorsInstanceID = x.ConnectionToNext.InstanceID;
						return false;
					}
					base.Owner.ClearAIUnavailableTargetsAtNode(x.Node.InstanceID);
					return true;
				});
				if (!Target.IsPlayer && pathNode2 != solutionEndNode2)
				{
					base.Owner.AddAIUnavailableTarget(pathNode2, Target.ObjectID);
				}
				if (ScanMode == 1)
				{
					base.ForceAnotherProcessPass = true;
				}
				else
				{
					ClearActivatorPathData();
				}
			}
			else
			{
				if (Result != null)
				{
					Result.Free();
					Result = null;
				}
				ResultSteps = 0;
				PathBasedOnTargetID = 0;
				ClearActivatorPathData();
				ScanMode = 0;
				if (m_currentChokePointValues != null)
				{
					m_currentChokePointValues.Clear();
					m_currentChokePointValues = null;
				}
			}
		}

		public void ClearActivatorScanPathData()
		{
			if (m_scanningActivatorResult != null)
			{
				m_scanningActivatorResult.Free();
				m_scanningActivatorResult = null;
			}
			m_scanningActivatorResultSteps = 0;
			m_scanningActivatorTarget = null;
		}

		public void ClearActivatorPathData()
		{
			if (ActivatorResult != null)
			{
				ActivatorResult.Free();
				ActivatorResult = null;
			}
			ActivatorResultSteps = 0;
			ActivatorTarget = null;
			HasActivator = false;
			if (m_possibleActivatorObjects != null)
			{
				m_possibleActivatorObjects.Clear();
			}
			m_possibleActivatorObjects = null;
			m_connectionWithActivators = null;
			ClearActivatorScanPathData();
		}

		public void StoreChokePointValues(SimpleLinkedList<ListPathPointNode> path)
		{
			if (base.Owner.CurrentTeam == Team.Independent || path == null)
			{
				return;
			}
			HashSet<int> hashSet = null;
			if (m_currentChokePointValues.Count > 0)
			{
				hashSet = new HashSet<int>(m_currentChokePointValues.Keys);
			}
			for (SimpleLinkedList<ListPathPointNode> simpleLinkedList = path; simpleLinkedList != null; simpleLinkedList = simpleLinkedList.Next)
			{
				if (simpleLinkedList.Item != null && simpleLinkedList.Item.Node != null && simpleLinkedList.Item.Node.ChokePoint)
				{
					hashSet?.Remove(simpleLinkedList.Item.Node.InstanceID);
					m_currentChokePointValues[simpleLinkedList.Item.Node.InstanceID] = simpleLinkedList.Item.Node.GetChokePointValue((int)base.Owner.CurrentTeam);
					simpleLinkedList.Item.Node.StoreChokePointValue((int)base.Owner.CurrentTeam, base.Owner.ObjectID, 4000f, base.Owner.BotBehaviorSet.ChokePointValue);
				}
			}
			if (hashSet == null || hashSet.Count <= 0)
			{
				return;
			}
			foreach (int item in hashSet)
			{
				m_currentChokePointValues.Remove(item);
			}
		}

		public override void ClearData()
		{
			if (Result != null)
			{
				Result.Free();
				Result = null;
			}
			Target = null;
			ClearActivatorPathData();
			if (m_currentChokePointValues != null)
			{
				m_currentChokePointValues.Clear();
				m_currentChokePointValues = null;
			}
		}
	}

	public class SandboxInstance
	{
		public string ScriptName { get; set; }

		public string ScriptInstanceID { get; set; }

		public string UniqueScriptInstanceID { get; set; }

		public Sandbox Sandbox { get; set; }

		public CancellationTokenSource SandboxCancellationTokenSource { get; set; }

		public string SandboxAssemblyLocation { get; set; }

		public string[] DebugFiles { get; set; }

		public SandboxInstance(string scriptName, string scriptInstanceID, string uniqueScriptInstanceID)
		{
			ScriptName = scriptName;
			ScriptInstanceID = scriptInstanceID;
			UniqueScriptInstanceID = uniqueScriptInstanceID;
			Sandbox = null;
			SandboxAssemblyLocation = null;
			DebugFiles = null;
			SandboxCancellationTokenSource = null;
		}

		public void DeleteAssembly()
		{
			if (!string.IsNullOrEmpty(SandboxAssemblyLocation) && File.Exists(SandboxAssemblyLocation))
			{
				Thread.Sleep(10);
				File.Delete(SandboxAssemblyLocation);
			}
			if (DebugFiles == null)
			{
				return;
			}
			string[] debugFiles = DebugFiles;
			foreach (string text in debugFiles)
			{
				Thread.Sleep(10);
				try
				{
					File.Delete(text);
					ConsoleOutput.ShowMessage(ConsoleOutputType.ScriptFiles, $"Script file '{text}' deleted");
				}
				catch (ThreadAbortException)
				{
					throw;
				}
				catch (Exception ex2)
				{
					ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Failed to delete script debug file {text}: {ex2.Message}");
				}
			}
		}
	}

	public delegate float UpdateGameWorldRun(int iterations, float chunkSizeTime, float time);

	public class GameWorldUpdateValues
	{
		public float UpdateThreadTimeLastValue;

		public float UpdateThreadTime;

		public float CurrentUpdateIterations;

		public float CurrentUpdateIterationTime;

		public float CurrentUpdateFPS = 60f;

		public void Reset()
		{
			CurrentUpdateIterations = 0f;
			CurrentUpdateFPS = 40f;
			UpdateThreadTimeLastValue = 0f;
			UpdateThreadTime = 0f;
			CurrentUpdateIterationTime = 25f;
		}
	}

	public enum GameOverType
	{
		Nobody,
		PlayerWins,
		TeamWins,
		TimesUp,
		Custom,
		SurvivalVictory,
		SurvivalLoss
	}

	public class GameOverResultUpdate
	{
		public GameOverResultData GameOverResult;

		public GameOverResultUpdate(GameOverResultData gameOverResult)
		{
			GameOverResult = gameOverResult;
		}
	}

	public class GameOverResultData
	{
		public GameOwnerEnum m_gameOwner;

		public bool IsOver { get; set; }

		public GameOverReason Reason { get; set; }

		public Team Team { get; set; }

		public string Text { get; set; }

		public List<int> WinningUserIdentifiers { get; set; }

		public GameOverType GameOverType { get; set; }

		public int GameOverTimeLeft { get; set; }

		public int GameOverVotes { get; set; }

		public int GameOverMaxVotes { get; set; }

		public bool GameOverTimeDone { get; set; }

		public bool GameOverContinueVotesDone { get; set; }

		public bool GameOverScoreUpdated { get; set; }

		public bool GameOverGibOnTimesUp { get; set; }

		public bool ChallengeCompleted { get; set; }

		public GameOverResultData(GameOwnerEnum gameOwner)
		{
			m_gameOwner = gameOwner;
			IsOver = false;
			Reason = GameOverReason.Default;
			Team = Team.Independent;
			GameOverType = GameOverType.Nobody;
			Text = "";
			GameOverContinueVotesDone = false;
			GameOverTimeDone = false;
			GameOverVotes = 0;
			GameOverMaxVotes = 0;
			GameOverTimeLeft = 0;
			GameOverScoreUpdated = false;
			WinningUserIdentifiers = new List<int>();
			ChallengeCompleted = false;
		}

		public void SetValues(GameOverResultData data)
		{
			IsOver = data.IsOver;
			Reason = data.Reason;
			Team = data.Team;
			Text = data.Text;
			WinningUserIdentifiers = data.WinningUserIdentifiers;
			GameOverType = data.GameOverType;
			GameOverTimeLeft = data.GameOverTimeLeft;
			GameOverVotes = data.GameOverVotes;
			GameOverMaxVotes = data.GameOverMaxVotes;
			GameOverContinueVotesDone = data.GameOverContinueVotesDone;
			GameOverTimeDone = data.GameOverTimeDone;
			GameOverGibOnTimesUp = data.GameOverGibOnTimesUp;
			GameOverScoreUpdated = data.GameOverScoreUpdated;
			ChallengeCompleted = data.ChallengeCompleted;
		}
	}

	public enum PlayingUserMode
	{
		None,
		Single,
		Multi
	}

	public enum GameOverReason
	{
		Default,
		TimesUp,
		Custom
	}

	[Flags]
	public enum DrawDebugFlags
	{
		None = 0,
		DrawActive = 1,
		DrawBackground = 2,
		DrawDebugMouseShape = 4,
		DrawDebugMouseData = 8,
		DrawDebugMouseLine = 0x10,
		DrawThumbnailViewFinder = 0x20,
		DrawScriptDebugInformation = 0x40
	}

	public class ExplosionData
	{
		public class ExplosionLineBuilder
		{
			public float Angle;

			public Microsoft.Xna.Framework.Vector2 Direction;

			public Box2D.XNA.RayCastInput RayCastInput;

			public List<ItemContainer<float, Fixture, ObjectData>> HitResults;

			public ExplosionLineBuilder(Microsoft.Xna.Framework.Vector2 dir, Box2D.XNA.RayCastInput rci)
			{
				Angle = (float)Math.Atan2(dir.Y, dir.X);
				Direction = dir;
				RayCastInput = rci;
				HitResults = new List<ItemContainer<float, Fixture, ObjectData>>();
			}

			public void SortHits()
			{
				if (HitResults.Count > 1)
				{
					HitResults.Sort((ItemContainer<float, Fixture, ObjectData> hitA, ItemContainer<float, Fixture, ObjectData> hitB) => hitA.Item1.CompareTo(hitB.Item1));
				}
			}
		}

		public class ExplosionLine
		{
			public float OriginalFraction { get; set; }

			public Box2D.XNA.RayCastInput RayCastInput { get; set; }

			public float Angle { get; set; }

			public Microsoft.Xna.Framework.Vector2 Direction { get; set; }

			public Box2D.XNA.RayCastInput RayCastInputToNext { get; set; }

			public override string ToString()
			{
				return $"A:{Angle} F:{OriginalFraction}";
			}

			public ExplosionLine(Microsoft.Xna.Framework.Vector2 sp, Microsoft.Xna.Framework.Vector2 ep, float originalFraction)
			{
				OriginalFraction = originalFraction;
				Microsoft.Xna.Framework.Vector2 vector = ep - sp;
				if (vector.IsValid())
				{
					Angle = (float)Math.Atan2(0f - vector.Y, vector.X);
					vector.Normalize();
				}
				else
				{
					Angle = 0f;
					vector = Microsoft.Xna.Framework.Vector2.Zero;
				}
				Direction = vector;
				RayCastInput = new Box2D.XNA.RayCastInput
				{
					p1 = sp,
					p2 = ep,
					maxFraction = 1f
				};
			}

			public void Fill(float amount)
			{
				Box2D.XNA.RayCastInput rayCastInput = RayCastInput;
				rayCastInput.p2 += Direction * amount;
				RayCastInput = rayCastInput;
			}
		}

		public enum HitType
		{
			None,
			Damage,
			Shockwave
		}

		public Microsoft.Xna.Framework.Vector2 ExplosionPosition { get; set; }

		public float ExplosionRadius { get; set; }

		public float ExplosionRadiusSquared { get; set; }

		public float ExplosionDamage { get; set; }

		public List<ExplosionLine> Lines { get; set; }

		public List<Pair<ObjectData, Explosion>> AffectedObjects { get; set; }

		public float Time { get; set; }

		public int Cycles { get; set; }

		public bool FirstCycle { get; set; }

		public int InstanceID { get; set; }

		public static float CalcExplosionImpactPercentage(float fraction)
		{
			float result = 1f;
			if (fraction > 0.55f)
			{
				float num = (1f - fraction) / 0.45f;
				result = num + 0.1f * (1f - num);
			}
			return result;
		}

		public ExplosionData(int explosionId, List<ExplosionLine> lines, Microsoft.Xna.Framework.Vector2 position, float radius, float damage)
		{
			InstanceID = explosionId;
			Lines = lines;
			ExplosionPosition = position;
			ExplosionRadius = radius;
			ExplosionRadiusSquared = radius * radius;
			ExplosionDamage = damage;
			Time = 0f;
			Cycles = 2;
			FirstCycle = true;
			AffectedObjects = new List<Pair<ObjectData, Explosion>>();
		}

		public void Dispose()
		{
			Lines.Clear();
			Lines = null;
		}

		public HitType CheckHitTest(ObjectData od, out Microsoft.Xna.Framework.Vector2 pos, out Microsoft.Xna.Framework.Vector2 dir, out float fraction)
		{
			pos = (od.IsPlayer ? (od.Body.GetPosition() + new Microsoft.Xna.Framework.Vector2(0f, Converter.ConvertWorldToBox2D(12f))) : od.Body.GetPosition());
			Fixture fixture = od.Body.GetFixtureList();
			while (true)
			{
				if (fixture != null)
				{
					if (fixture.TestPoint(ExplosionPosition))
					{
						break;
					}
					fixture = fixture.GetNext();
					continue;
				}
				dir = pos - ExplosionPosition;
				float num = dir.LengthSquared();
				dir.Normalize();
				SFDMath.RotatePosition(ref dir, 0f - od.Body.GetAngle(), out var rotatedPosition);
				for (Fixture fixture2 = od.Body.GetFixtureList(); fixture2 != null; fixture2 = fixture2.GetNext())
				{
					Microsoft.Xna.Framework.Vector2 supportVertex = fixture2.GetShape().GetSupportVertex(-rotatedPosition);
					Microsoft.Xna.Framework.Vector2 worldPoint = fixture2.GetBody().GetWorldPoint(supportVertex);
					float num2 = (ExplosionPosition - worldPoint).LengthSquared();
					if (num2 < num)
					{
						num = num2;
					}
				}
				if (dir.IsValid())
				{
					if (num > ExplosionRadiusSquared)
					{
						dir.Normalize();
						fraction = 1f;
						return HitType.None;
					}
					float angle = (float)Math.Atan2(0f - dir.Y, dir.X);
					ItemContainer<ExplosionLine, ExplosionLine> linesBetweenAngle = GetLinesBetweenAngle(angle);
					float num3 = (SFDMath.LineLineIntersection(ExplosionPosition, pos, linesBetweenAngle.Item1.RayCastInputToNext.p1, linesBetweenAngle.Item1.RayCastInputToNext.p2) - ExplosionPosition).LengthSquared();
					fraction = (float)Math.Sqrt(num) / ExplosionRadius;
					dir = pos - ExplosionPosition;
					if (dir.IsValid())
					{
						dir.Normalize();
					}
					else
					{
						dir = Microsoft.Xna.Framework.Vector2.UnitY;
					}
					if (!(num <= num3))
					{
						return HitType.Shockwave;
					}
					return HitType.Damage;
				}
				dir = Microsoft.Xna.Framework.Vector2.UnitY;
				fraction = 0f;
				return HitType.Damage;
			}
			dir = Microsoft.Xna.Framework.Vector2.UnitY;
			fraction = 0f;
			return HitType.Damage;
		}

		public ItemContainer<ExplosionLine, ExplosionLine> GetLinesBetweenAngle(float angle)
		{
			ExplosionLine explosionLine = Lines[0];
			int num = 1;
			while (true)
			{
				if (num < Lines.Count)
				{
					if (Lines[num].Angle > angle && !(explosionLine.Angle >= angle))
					{
						break;
					}
					explosionLine = Lines[num];
					num++;
					continue;
				}
				return new ItemContainer<ExplosionLine, ExplosionLine>(Lines[Lines.Count - 1], Lines[0]);
			}
			return new ItemContainer<ExplosionLine, ExplosionLine>(explosionLine, Lines[num]);
		}

		public List<ExplosionLine> GetCloseLinesFromAngle(Microsoft.Xna.Framework.Vector2 dir)
		{
			List<ExplosionLine> list = new List<ExplosionLine>();
			for (int i = 0; i < Lines.Count; i++)
			{
				if (Microsoft.Xna.Framework.Vector2.Dot(Lines[i].Direction, dir) > 0.3f)
				{
					list.Add(Lines[i]);
				}
			}
			return list;
		}

		public void AffectObjectWithEffects(ObjectData od, Explosion explosion)
		{
			if (explosion.ExplosionImpactPercentage > 0.6f)
			{
				od.SetSmokeTime(3000f);
			}
		}

		public bool AffectObjectFire(ObjectData od)
		{
			if (od.DoExplosionHit && CheckHitTest(od, out var _, out var _, out var _) == HitType.Damage)
			{
				od.SetMaxFire();
				return true;
			}
			return false;
		}

		public bool AffectObject(ObjectData od, int explosionCount)
		{
			if (od.DoExplosionHit)
			{
				Microsoft.Xna.Framework.Vector2 pos;
				Microsoft.Xna.Framework.Vector2 dir;
				float fraction;
				HitType hitType = CheckHitTest(od, out pos, out dir, out fraction);
				if (hitType != HitType.None)
				{
					Explosion explosion = new Explosion(InstanceID, dir, pos, od.Body.GetFixtureList(), CalcExplosionImpactPercentage(fraction), Converter.ConvertBox2DToWorld(ExplosionPosition), Converter.ConvertBox2DToWorld(ExplosionRadius), ExplosionDamage, hitType);
					if (hitType == HitType.Shockwave)
					{
						explosion.SourceExplosionDamage = 0f;
					}
					else if (od.IsPlayer && ((Player)od.InternalData).IgnoreExplosionCountDamage >= explosionCount)
					{
						explosion.SourceExplosionDamage = 0f;
						explosion.HitType = HitType.Shockwave;
					}
					ExplosionBeforeHitEventArgs e = new ExplosionBeforeHitEventArgs();
					od.BeforeExplosionHit(explosion, e);
					if (!e.Cancel)
					{
						if (hitType == HitType.Damage)
						{
							AffectObjectWithEffects(od, explosion);
						}
						ExplosionHitEventArgs e2 = new ExplosionHitEventArgs();
						od.ExplosionHit(explosion, e2);
						AffectedObjects.Add(new Pair<ObjectData, Explosion>(od, explosion));
						if (od.GameOwner != GameOwnerEnum.Client && od.Tile != null && od.Tile.CanBeMissile)
						{
							od.GameWorld.AddMissileObject(od, ObjectMissileStatus.Debris);
							if (od.MissileData != null)
							{
								od.MissileData.ResetHitCooldown();
							}
						}
					}
					return true;
				}
			}
			return false;
		}
	}

	public class ExplosionSource
	{
		public float Radius { get; set; }

		public Microsoft.Xna.Framework.Vector2 Position { get; set; }

		public int CycleCount { get; set; }

		public float Time { get; set; }

		public Microsoft.Xna.Framework.Vector2 WorldPosition { get; set; }

		public float WorldRadius { get; set; }

		public float ExplosionDamage { get; set; }

		public ExplosionSource(Microsoft.Xna.Framework.Vector2 pos, float r, Microsoft.Xna.Framework.Vector2 wp, float wr, float ed)
		{
			Position = pos;
			Radius = r;
			CycleCount = 2;
			Time = 0f;
			WorldPosition = wp;
			WorldRadius = wr;
			ExplosionDamage = ed;
		}
	}

	public class PlayerSyncTransformData
	{
		public Body Body;

		public Microsoft.Xna.Framework.Vector2 Position;

		public Microsoft.Xna.Framework.Vector2 Velocity;

		public bool DoSync;

		public static GenericClassPool<PlayerSyncTransformData> m_memPool = new GenericClassPool<PlayerSyncTransformData>(() => new PlayerSyncTransformData(), 4);

		public void Free()
		{
			Body = null;
			DoSync = false;
			m_memPool.FlagFreeItem(this);
		}

		public static PlayerSyncTransformData GetInstance()
		{
			return m_memPool.GetFreeItem();
		}
	}

	public struct BodyDataTransformInfo
	{
		public Microsoft.Xna.Framework.Vector2 OriginalPosition;

		public Microsoft.Xna.Framework.Vector2 OriginalVelocity;

		public float OriginalAngle;

		public Microsoft.Xna.Framework.Vector2 VelocityDiff;

		public Microsoft.Xna.Framework.Vector2 PositionDiff;

		public float VelocityDiffLength;

		public float AngularVelocityDiff;

		public float PositionDiffLength;

		public float AngleDiff;

		public void SetData(Body body, NetMessage.ObjectPositionUpdate.Data posData, Microsoft.Xna.Framework.Vector2 realPosition)
		{
			OriginalPosition = body.GetPosition();
			OriginalVelocity = body.GetLinearVelocity();
			OriginalAngle = body.GetAngle();
			VelocityDiff = posData.Velocity - body.GetLinearVelocity();
			PositionDiff = realPosition - body.GetPosition();
			VelocityDiffLength = VelocityDiff.Length();
			AngularVelocityDiff = posData.AngularVelocity - body.GetAngularVelocity();
			PositionDiffLength = PositionDiff.Length();
			AngleDiff = posData.Angle - body.GetAngle();
		}
	}

	public enum CheckRelativePositionClippingDirectionEnum
	{
		Down,
		Up,
		Left,
		Right
	}

	public enum TunnelingCheckType
	{
		None,
		FeetToProjectileBase,
		ProjectileBaseToSpawnPosition
	}

	public struct RayCastResult
	{
		public bool TunnelCollision;

		public Microsoft.Xna.Framework.Vector2 StartPosition;

		public Microsoft.Xna.Framework.Vector2 EndPosition;

		public Microsoft.Xna.Framework.Vector2 Direction;

		public float TotalDistance;

		public float Fraction;

		public Fixture EndFixture;
	}

	public delegate bool RayCastFixtureCheck(Fixture fixture);

	public delegate bool RayCastPlayerCheck(Player player);

	public class FullScriptSanitizeError
	{
		public string Message;

		public int Row;
	}

	public class ScriptEventException
	{
		public Exception e;

		public string message;

		public string method;

		public string formattedStackTrace;

		public ScriptEventException(Exception e)
		{
			this.e = e;
			if (e.InnerException != null)
			{
				this.e = e.InnerException;
			}
			message = this.e.Message;
			ParseMethodInfo();
		}

		public void SetMethodFromTypeName(string typeName)
		{
			int num = typeName.LastIndexOf("+");
			if (num >= 0)
			{
				method = typeName.Substring(num + 1, typeName.Length - 1 - num);
			}
			else
			{
				method = typeName;
			}
		}

		public void ParseMethodInfo()
		{
			string text = e.StackTrace;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "<stacktrace unavailable>";
			}
			int num = text.LastIndexOf("SFDScript.GameScript.");
			if (num >= 0)
			{
				text = text.Replace("\r\n", "\n");
				int num2 = text.IndexOfAny(new char[2] { '\n', '\r' }, num);
				if (num2 >= 0)
				{
					text = text.Remove(num2);
				}
				num2 = text.IndexOfAny(new char[2] { '\n', '\r' });
				if (num2 == -1)
				{
					num2 = text.Length;
				}
				method = text.Substring(0, num2);
				while (method.StartsWith(" "))
				{
					method = method.Remove(0, 1);
				}
				if (method.StartsWith("at "))
				{
					method = method.Remove(0, "at ".Length);
				}
				if (method.StartsWith("SFDScript.GameScript."))
				{
					method = method.Remove(0, "SFDScript.GameScript.".Length);
				}
				text = text.Replace("\n", "\r\n");
			}
			else
			{
				method = "<method unavailable>";
			}
			formattedStackTrace = text;
			formattedStackTrace = formattedStackTrace.Replace(" SFDScript.GameScript.", " ");
		}
	}

	public struct ScriptDebugLine(Microsoft.Xna.Framework.Vector2 p1, Microsoft.Xna.Framework.Vector2 p2, Microsoft.Xna.Framework.Color c)
	{
		public Microsoft.Xna.Framework.Vector2 Point1 = p1;

		public Microsoft.Xna.Framework.Vector2 Point2 = p2;

		public Microsoft.Xna.Framework.Color Color = c;
	}

	public struct ScriptDebugCircle(Microsoft.Xna.Framework.Vector2 p1, float r, Microsoft.Xna.Framework.Color c)
	{
		public Microsoft.Xna.Framework.Vector2 Center = p1;

		public float Radius = r;

		public Microsoft.Xna.Framework.Color Color = c;
	}

	public struct ScriptDebugArea(AABB aabb, Microsoft.Xna.Framework.Color c)
	{
		public AABB AABB = aabb;

		public Microsoft.Xna.Framework.Color Color = c;
	}

	public struct ScriptDebugText(string text, Microsoft.Xna.Framework.Vector2 position, Microsoft.Xna.Framework.Color c)
	{
		public string Text = text;

		public Microsoft.Xna.Framework.Vector2 Position = position;

		public Microsoft.Xna.Framework.Color Color = c;
	}

	public class PendingStorageOutput
	{
		public string FilePath { get; set; }

		public LocalStorageOutput Output { get; set; }

		public PendingStorageOutput(string filePath, LocalStorageOutput storage)
		{
			FilePath = filePath;
			Output = storage;
		}
	}

	public class ScriptCallbackEvent
	{
		public string ScriptInstanceUniqueID;

		public Events.CallbackDelegate Func;

		public float LastElapsedTotalTime;

		public bool Active;

		public ScriptCallbackEvent(string scriptInstanceUniqueID, Events.CallbackDelegate func, float lastElapsedTotalTime)
		{
			ScriptInstanceUniqueID = scriptInstanceUniqueID;
			Func = func;
			LastElapsedTotalTime = lastElapsedTotalTime;
			Active = true;
		}
	}

	public class ScriptCallbackEvent_Update : ScriptCallbackEvent
	{
		public new Events.UpdateCallback Func;

		public uint Interval;

		public ScriptCallbackEvent_Update(string scriptInstanceUniqueID, Events.UpdateCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
			Interval = func.Interval;
		}
	}

	public class ScriptCallbackEvent_PlayerCreated : ScriptCallbackEvent
	{
		public new Events.PlayerCreatedCallback Func;

		public ScriptCallbackEvent_PlayerCreated(string scriptInstanceUniqueID, Events.PlayerCreatedCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_PlayerDamage : ScriptCallbackEvent
	{
		public new Events.PlayerDamageCallback Func;

		public ScriptCallbackEvent_PlayerDamage(string scriptInstanceUniqueID, Events.PlayerDamageCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_PlayerDeath : ScriptCallbackEvent
	{
		public new Events.PlayerDeathCallback Func;

		public ScriptCallbackEvent_PlayerDeath(string scriptInstanceUniqueID, Events.PlayerDeathCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_ProjectileHit : ScriptCallbackEvent
	{
		public new Events.ProjectileHitCallback Func;

		public ScriptCallbackEvent_ProjectileHit(string scriptInstanceUniqueID, Events.ProjectileHitCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_ProjectileCreated : ScriptCallbackEvent
	{
		public new Events.ProjectileCreatedCallback Func;

		public ScriptCallbackEvent_ProjectileCreated(string scriptInstanceUniqueID, Events.ProjectileCreatedCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_ExplosionHit : ScriptCallbackEvent
	{
		public new Events.ExplosionHitCallback Func;

		public ScriptCallbackEvent_ExplosionHit(string scriptInstanceUniqueID, Events.ExplosionHitCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_ObjectDamage : ScriptCallbackEvent
	{
		public new Events.ObjectDamageCallback Func;

		public ScriptCallbackEvent_ObjectDamage(string scriptInstanceUniqueID, Events.ObjectDamageCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_ObjectTerminated : ScriptCallbackEvent
	{
		public new Events.ObjectTerminatedCallback Func;

		public ScriptCallbackEvent_ObjectTerminated(string scriptInstanceUniqueID, Events.ObjectTerminatedCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_ObjectCreated : ScriptCallbackEvent
	{
		public new Events.ObjectCreatedCallback Func;

		public ScriptCallbackEvent_ObjectCreated(string scriptInstanceUniqueID, Events.ObjectCreatedCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_UserMessage : ScriptCallbackEvent
	{
		public new Events.UserMessageCallback Func;

		public ScriptCallbackEvent_UserMessage(string scriptInstanceUniqueID, Events.UserMessageCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_PlayerMeleeAction : ScriptCallbackEvent
	{
		public new Events.PlayerMeleeActionCallback Func;

		public ScriptCallbackEvent_PlayerMeleeAction(string scriptInstanceUniqueID, Events.PlayerMeleeActionCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_PlayerWeaponRemovedAction : ScriptCallbackEvent
	{
		public new Events.PlayerWeaponRemovedActionCallback Func;

		public ScriptCallbackEvent_PlayerWeaponRemovedAction(string scriptInstanceUniqueID, Events.PlayerWeaponRemovedActionCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_PlayerWeaponAddedAction : ScriptCallbackEvent
	{
		public new Events.PlayerWeaponAddedActionCallback Func;

		public ScriptCallbackEvent_PlayerWeaponAddedAction(string scriptInstanceUniqueID, Events.PlayerWeaponAddedActionCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_PlayerKeyInput : ScriptCallbackEvent
	{
		public new Events.PlayerKeyInputCallback Func;

		public ScriptCallbackEvent_PlayerKeyInput(string scriptInstanceUniqueID, Events.PlayerKeyInputCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_UserJoin : ScriptCallbackEvent
	{
		public new Events.UserJoinCallback Func;

		public ScriptCallbackEvent_UserJoin(string scriptInstanceUniqueID, Events.UserJoinCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class ScriptCallbackEvent_UserLeave : ScriptCallbackEvent
	{
		public new Events.UserLeaveCallback Func;

		public ScriptCallbackEvent_UserLeave(string scriptInstanceUniqueID, Events.UserLeaveCallback func, float lastElapsedTotalTime)
			: base(scriptInstanceUniqueID, func, lastElapsedTotalTime)
		{
			Func = func;
		}
	}

	public class GameTimer
	{
		public const int MAIN_TIMER = 1;

		public const int SUDDEN_DEATH_TIMER = 2;

		public float m_timeRemainingMs;

		public GameWorld GameWorld { get; set; }

		public TimeSpan TimeRemaining => TimeSpan.FromMilliseconds(m_timeRemainingMs);

		public float TimeRemainingMs => m_timeRemainingMs;

		public string Description { get; set; }

		public bool Changed { get; set; }

		public bool IsOver { get; set; }

		public int ID { get; set; }

		public bool Disposed { get; set; }

		public GameTimer(int id, GameWorld gameWorld, float timeRemainingMs, string description)
		{
			ID = id;
			Disposed = false;
			GameWorld = gameWorld;
			IsOver = false;
			UpdateTimeRemaining(timeRemainingMs);
			UpdateDescription(description);
		}

		~GameTimer()
		{
		}

		public virtual void Dispose()
		{
			Disposed = true;
			GameWorld = null;
		}

		public void UpdateTimeRemaining(float timeRemainingMs)
		{
			if (m_timeRemainingMs != timeRemainingMs)
			{
				m_timeRemainingMs = timeRemainingMs;
				Changed = true;
				if (m_timeRemainingMs > 0f)
				{
					IsOver = false;
				}
			}
		}

		public void UpdateDescription(string description)
		{
			if (Description != description)
			{
				Description = description;
				Changed = true;
			}
		}

		public virtual void Update(float ms)
		{
			if (!(m_timeRemainingMs > 0f))
			{
				return;
			}
			m_timeRemainingMs -= ms;
			if (m_timeRemainingMs <= 0f)
			{
				m_timeRemainingMs = 0f;
				if (!IsOver)
				{
					IsOver = true;
					Changed = true;
					TimerOver();
				}
			}
		}

		public virtual void TimerOver()
		{
		}
	}

	public class GameTimerMain : GameTimer
	{
		public GameTimerMain(float timeRemainingMs, GameWorld gameWorld)
			: base(1, gameWorld, timeRemainingMs, "Main timer")
		{
		}

		public override void Update(float ms)
		{
			base.Update(ms);
			if (base.GameWorld.GameOverData.IsOver)
			{
				base.IsOver = true;
			}
		}

		public override void TimerOver()
		{
			if (base.GameWorld.GameOwner != GameOwnerEnum.Client && base.GameWorld.m_suddenDeathTimer == null)
			{
				base.GameWorld.SetGameOver(GameOverReason.TimesUp);
			}
		}

		public override void Dispose()
		{
			base.Dispose();
		}
	}

	public class GameTimerSuddenDeath : GameTimer
	{
		public GameTimerSuddenDeath(float timeRemainingMs, GameWorld gameWorld)
			: base(2, gameWorld, timeRemainingMs, "Sudden death timer")
		{
		}

		public override void Update(float ms)
		{
			base.Update(ms);
			if (base.GameWorld.GameOverData.IsOver)
			{
				base.IsOver = true;
			}
		}

		public override void TimerOver()
		{
			if (base.GameWorld.GameOwner != GameOwnerEnum.Client && base.GameWorld.CheckOpponentsRemaining())
			{
				base.GameWorld.GameOverData.GameOverGibOnTimesUp = true;
				base.GameWorld.SetGameOver(GameOverReason.TimesUp);
			}
		}

		public override void Dispose()
		{
			base.Dispose();
		}
	}

	public List<List<EditHistoryItemBase>> m_editHistoryActions;

	public List<List<EditHistoryItemBase>> m_editHistoryActionsUndone;

	public SelectionFilter m_editSelectionFitler = new SelectionFilter();

	public ObjectPropertyInstance m_editTargetObjectPropertyInstance;

	public List<ObjectPropertyInstance> m_editTargetObjectPropertyInstances;

	public Tile.SIZEABLE m_sizeableOut;

	public Tile.SIZEABLE m_sizeableOutLastNonN;

	public ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>[] m_sizeableCursorsDirections;

	public int m_editHistoryGroupLevel;

	public List<EditHistoryItemBase> m_editHistoryGroupItems;

	public bool m_editNotAllowedRunning;

	public const string m_editHistoryErrorMessage = "\r\nEdit history failed for unknown reason.\r\nHistory has been cleared to avoid crash.";

	public Dictionary<string, int> m_editSelectedLayers;

	public List<Microsoft.Xna.Framework.Vector2> m_editPreviewOffsets = new List<Microsoft.Xna.Framework.Vector2>();

	public const ushort MAX_GROUP_COUNT = 65000;

	public Microsoft.Xna.Framework.Vector2 m_editMouseDownWorldPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public List<Microsoft.Xna.Framework.Vector2> m_editSelectionPositionBeforeMove = new List<Microsoft.Xna.Framework.Vector2>();

	public EditLeftMoveAction m_editLeftMoveAction = EditLeftMoveAction.None;

	public static List<Microsoft.Xna.Framework.Vector2> EditCopyOffsetPositions = new List<Microsoft.Xna.Framework.Vector2>();

	public static ushort EditCopyObjectsInEditGroupID = 0;

	public static List<object[]> EditCopyObjects = new List<object[]>();

	public Tile.SIZEABLE m_editResizeSizeable;

	public EditHistoryItemObjectData m_editResizeDataBefore;

	public List<EditHistoryItemObjectData> m_editRotationStatusesBeforeRotate = new List<EditHistoryItemObjectData>();

	public Microsoft.Xna.Framework.Vector2 m_editRotationPositionCenter = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Vector2 m_editRotationMouseStartPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public bool m_editRotationInAction;

	public PlayerAIPackage m_botAIPackageForceReProcess;

	public List<ObjectData> m_AITargetableObjects;

	public Area m_activeCameraSafeArea;

	public Area m_activeCameraMaxArea;

	public CachedObjectData<ObjectCameraAreaTrigger> m_activeObjectCameraAreaTrigger;

	public CameraAreaPackage m_worldCameraPackage;

	public List<ObjectData> m_activeSecondaryCameraTargetsAll;

	public static float m_individualZoom = 50f;

	public const float m_individualZoomForce = 0.3f;

	public const float MAX_INDIVIDUAL_ZOOM = 30f;

	public float m_maxIndividualZoom = 30f;

	public float m_minIndividualZoom = m_individualZoom;

	public float FixedIndividualZoom = -1f;

	public Player m_spectatingPlayer;

	public int m_lastSpectateIndex;

	public const float SpectateCooldown = 3000f;

	public float m_spectatePlayerCooldown = 3000f;

	public bool CanManuallyChangeSpectatingPlayer;

	public List<Player> m_validPlayersToSpectate;

	public Area m_primaryTargetArea;

	public Area m_dynamicArea;

	public Area m_staticArea;

	public Area m_individualArea;

	public Area m_currentCameraFocusArea;

	public Microsoft.Xna.Framework.Vector2 m_staticAreaCenterPos = Microsoft.Xna.Framework.Vector2.Zero;

	public bool m_staticSnapPos;

	public ExpanderArea m_currentSafeAreaDelayed;

	public ExpanderArea m_currentCameraDelayedArea;

	public ExpanderArea m_currentCameraDelayedKeepPlayerInsideArea;

	public ExpanderArea m_currentCameraDelayedIndividualArea;

	public CameraAreaCalculateHelper m_dynamicCameraHelper = new CameraAreaCalculateHelper();

	public CameraMode m_allowedCameraModes = CameraMode.Dynamic | CameraMode.Static | CameraMode.Individual;

	public AABB m_worldCurrentDrawZone;

	public AABB m_worldRedrawZoneInner;

	public AABB m_worldRedrawZoneOuter;

	public AABB m_worldCurrentFarBGDrawZone;

	public List<Player> m_newlyCreatedPlayers = new List<Player>();

	public List<ObjectData> m_newlyCreatedObjects = new List<ObjectData>();

	public Dictionary<ushort, List<ObjectData>> m_groupSyncCreatedObjects;

	public FixtureDef m_fd;

	public BodyDef m_bd;

	public Dictionary<int, Queue<ObjectDataSyncedMethod>> m_storedObjectDataSyncedMethods;

	public static object m_loadResource = new object();

	public GameSFD m_game;

	public Microsoft.Xna.Framework.Color m_fogColor = new Microsoft.Xna.Framework.Color(15f, 15f, 15f, 0.015f);

	public bool m_deathSequenceInitialized;

	public float m_deathSequenceFadeInTimer;

	public VoteCounter m_voteCounter;

	public VictoryText m_victoryText;

	public const float RECENT_EXPLOSION_LIFETIME = 150f;

	public Dictionary<float, float> m_elapsedTotalGameTimeTimestamps;

	public World b2_world_active;

	public World b2_world_background;

	public Box2DSettings b2_settings;

	public DebugDraw b2_debugDraw;

	public bool b2_flagsSet;

	public VertexDeclaration b2_vertexDecl;

	public BasicEffect b2_simpleColorEffect;

	public static int m_uniqueGameWorldID = 0;

	public List<UserScriptBridge> UserScriptBridges = new List<UserScriptBridge>();

	public List<IObject> m_queueScriptBridgeDispose;

	public object m_disposeLockObject = new object();

	public bool m_disposed;

	public static CameraMode CurrentCameraMode = CameraMode.Dynamic;

	public bool m_firstUpdateIsRun;

	public double m_drawingBox2DSimulationTimestepOverModifier = 1.0;

	public double m_drawingBox2DSimulationTimestepOverLastNetTime;

	public double m_drawingBox2DSimulationTimestepOverBase;

	public double m_drawingBox2DSimulationTimestepOver_property;

	public volatile float DrawingBox2DSimulationTimestepOver;

	public float m_box2DsimulationTimeOver;

	public float m_lastUpdateTime;

	public float m_nextHeartbeatDelay = 1f;

	public float m_lastBox2DTimeStep;

	public float m_gameOverNextCheck;

	public float m_gameOverDelayTime;

	public bool m_gameOverSignalSent;

	public float m_gameOverAutomaticallyRestartTimer;

	public float m_gameOverLastAutoSecondSend;

	public float m_gameOverVoteDoneDelayTimer;

	public bool m_gameOverVoteDoneMajorityIsReady;

	public ObjectData m_confirmDeletionObject;

	public double m_confirmDeletionObjectTime;

	public List<ObjectData> m_clientHeathChecks;

	public CachedPlayerKey m_localGameUserPlayerCache;

	public Microsoft.Xna.Framework.Vector2 m_debugOrgCamPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public float m_debugOrgCamZoom = 1f;

	public byte m_playerPrecacheItemsState;

	public ObjectData m_debugMouseObject;

	public MouseJoint m_debugMouseJoint;

	public World m_debugMouseWorld;

	public SimpleLinkedList<ListPathPointNode> m_debugPathNodePath;

	public float m_debugPathNodePathRenderTime;

	public bool m_debugPathNodeDrawing;

	public Microsoft.Xna.Framework.Vector2 m_debugPathNodeStartBox2DPos = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Vector2 m_debugPathNodeEndBox2DPos = Microsoft.Xna.Framework.Vector2.Zero;

	public List<ObjectData> m_debrisCurrent;

	public List<ObjectData> m_debrisNew;

	public List<DialogueItem> m_newDialogues;

	public List<DialogueItem> m_dialogues;

	public List<DialogueItem> m_closedDialogues;

	public HashSet<int> m_addedDialogueIDs;

	public int m_nextDialogueID;

	public const float EXPLOSION_WORLD_RADIUS = 36f;

	public const ushort MAX_EXPLOSIONS_PER_CYCLE = 1;

	public int m_explosionDataInstanceID;

	public int m_fireplosionDataInstanceID;

	public int m_currentExplosionCount;

	public List<ExplosionData> m_recentExplosions;

	public Queue<ItemContainer<Microsoft.Xna.Framework.Vector2, float, bool, int>> m_queuedExplosions;

	public ushort m_explosionsTriggeredThisCycle;

	public HashSet<int> m_createdFireNodes = new HashSet<int>();

	public HashSet<int> m_removedFireNodes = new HashSet<int>();

	public List<ObjectImpulsePoints> m_objectImpulsePointsList;

	public Dictionary<ObjectData, ObjectImpulsePoints> m_objectImpulsePointsDictionary;

	public GenericClassPool<ObjectImpulsePoints> m_objectImpulsePointsPool;

	public float m_gibbingImpulseSlowmotionModifier = 1f;

	public float m_gibbingImpulseSlowmotionModifierBeforeCorrection;

	public ObjectGibbGroups m_gibbingObjectGroups;

	public int m_gibbingCurrentFrameCounter;

	public Dictionary<Fixture, float> m_gibForces = new Dictionary<Fixture, float>();

	public Texture2D m_transitionCircle;

	public float m_transitionRadius = 4f;

	public bool m_showTransition = true;

	public bool m_gameOverInitialized;

	public Microsoft.Xna.Framework.Point m_transitionScreenCenter;

	public FixedArray8B<PlayerHUD> PlayerHUDs;

	public StartupSequence m_startupText;

	public float m_deathTextBlinkTimer;

	public bool m_deathTextBlink;

	public const float TEXT_HEIGHT_OFFSET = 5f / 6f;

	public string m_deathText = "";

	public Microsoft.Xna.Framework.Vector2 m_deathTextSize = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Vector2 m_deathTextPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Color m_deathTextColor = ColorCorrection.PremultipliedAlphaColor(Constants.COLORS.DEATH_TEXT, 1f);

	public string m_spectatingText = "";

	public Microsoft.Xna.Framework.Vector2 m_spectatingTextSize = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Vector2 m_spectatingTextPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public string m_currentlySpectatingText = "";

	public Microsoft.Xna.Framework.Vector2 m_currentlySpectatingTextSize = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Vector2 m_currentlySpectatingTextPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public string m_spectatingTipText = "";

	public Microsoft.Xna.Framework.Vector2 m_spectatingTipTextSize = Microsoft.Xna.Framework.Vector2.Zero;

	public Microsoft.Xna.Framework.Vector2 m_spectatingTipTextPosition = Microsoft.Xna.Framework.Vector2.Zero;

	public Player PrimaryLocalPlayer;

	public FixedArray8B<Player> LocalPlayers;

	public FixedArray8B<GameUser> LocalGameUsers;

	public FixedArray8B<PlayerStatus> LocalPlayerStatus;

	public float m_nextTimeUpdate;

	public int m_timerTickLastSecond;

	public float m_timerTickPitch;

	public float m_suddenDeathBlinkTimer;

	public bool m_suddenDeathBlink;

	public Dictionary<int, Queue<NetMessage.PlayerReceiveItem.Data>> m_queuedPlayerReceiveItemUpdates = new Dictionary<int, Queue<NetMessage.PlayerReceiveItem.Data>>();

	public Dictionary<int, NetMessage.PlayerUpdateMetaData.Data> m_queuedPlayerMetaDataUpdates = new Dictionary<int, NetMessage.PlayerUpdateMetaData.Data>();

	public Dictionary<int, NetMessage.PlayerUpdateModifierData.Data> m_queuedPlayerModifierDataUpdates = new Dictionary<int, NetMessage.PlayerUpdateModifierData.Data>();

	public bool m_restartInstant;

	public List<ObjectData> m_kickedObjects;

	public List<Player> m_meleePlayersList;

	public List<Player> m_kickPlayersList;

	public const float MISSILE_MIN_HIT_SPEED = 7f;

	public Box2D.XNA.RayCastInput[] m_missileRCICache;

	public NetOutgoingMessage m_multiPacket;

	public Dictionary<Player, PlayerSyncTransformData> m_positionUpdatePlayerSyncTransformData = new Dictionary<Player, PlayerSyncTransformData>();

	public int m_projectileWorldId;

	public const string SCRIPT_HEADER = "using System;\r\nusing System.Linq;\r\nusing System.Collections;\r\nusing System.Collections.Generic;\r\nusing System.Text;\r\nusing System.Text.RegularExpressions;\r\nusing SFDGameScriptInterface;\r\n\r\nnamespace SFDScript\r\n{\r\n    public static class SFD\r\n    {\r\n        public static IGame Game { get { return GameScript.Game; } }\r\n    }\r\n    \r\n    public class GameScript : GameScriptInterface\r\n    {\r\n        // Cancellation token used for cooperative cancellation of scripts, set by the sandbox environment.\r\n        public static System.Threading.CancellationToken __sandboxCancellationToken;\r\n\r\n        // Needs to be static for script compatability reasons.\r\n        // Static needs to live in this GameScript class to be isolated to compiled assemblies.\r\n        private static IGame __game = null;\r\n        public static IGame Game { get { return __game; } }\r\n\r\n        protected override void __onDispose() { __game = null; }\r\n\r\n        // SFDScript.GameScript\r\n        public GameScript(IGame game) : base() { __game = game; }\r\n";

	public const string SCRIPT_FOOTER = "\r\n    }\r\n}";

	public string m_script = "";

	public bool m_scriptRegisteredUpdateCallbacksDirty;

	public ScriptCallbackEvent[] m_scriptRegisteredUpdateCallbacksArray;

	public List<ScriptCallbackEvent> m_scriptRegisteredUpdateCallbacks;

	public List<Tuple<ObjectData, ObjectData.DamageType, float, int>> m_queuedRunScriptOnObjectDamageCallbacks;

	public static int m_uniqueExtensionScriptInstanceNamePostfix = 0;

	public HashSet<string> m_failedScriptMethods;

	public bool m_failedScriptCompilation;

	public HashSet<string> m_scriptsRunOnShutdown = new HashSet<string>();

	public bool m_scriptDebugInfoDrawn;

	public ScriptDebugLine[] m_scriptDebugLines;

	public int m_scriptDebugLinesCount;

	public ScriptDebugCircle[] m_scriptDebugCircles;

	public int m_scriptDebugCirclesCount;

	public ScriptDebugArea[] m_scriptDebugAreas;

	public int m_scriptDebugAreasCount;

	public ScriptDebugText[] m_scriptDebugTexts;

	public int m_scriptDebugTextsCount;

	public static object m_sessionStorageLock = new object();

	public Dictionary<string, LocalStorage> m_sessionStorageEntries;

	public Dictionary<string, LocalStorage> m_permanentStorageEntries;

	public Dictionary<string, LocalStorage> m_sharedStorageEntries;

	public static Queue<PendingStorageOutput> m_pendingStorageOutputQueue = new Queue<PendingStorageOutput>();

	public static bool m_pendingStorageOutputRunning = false;

	public static int m_responsiblePendingStorageManagedThreadId = 0;

	public static HashSet<LocalStorage> m_pendingStorageSaves = new HashSet<LocalStorage>();

	public static object m_pendingStorageSavesLock = new object();

	public static LocalStorage m_pendingStorageLoad = null;

	public static AutoResetEvent m_pendingStorageLoadWH = new AutoResetEvent(initialState: false);

	public static AutoResetEvent m_pendingStorageIOProcessWH = new AutoResetEvent(initialState: false);

	public Dictionary<string, string> m_sessionDataEntries;

	public List<GameTimer> Timers;

	public GameTimerMain m_mainTimer;

	public GameTimerSuddenDeath m_suddenDeathTimer;

	public bool m_suddenDeathTwoOpponentsRemainingCheckPerformed;

	public bool m_suddenDeathSpawnFrenzyPerformed;

	public bool m_mainTimerActive;

	public bool m_forcedSuddenDeathStarted;

	public float m_lastTimerUpdate;

	public Dictionary<int, HashSet<BaseObject>> m_activatedTriggers;

	public Dictionary<int, HashSet<BaseObject>> m_queuedTriggersWithSenders;

	public Queue<Pair<ObjectTriggerBase, BaseObject>> m_queuedTriggers;

	public bool EditDrawCenter { get; set; }

	public bool EditDrawWorldZones { get; set; }

	public bool EditDrawPathGrid { get; set; }

	public bool EditDrawGrid { get; set; }

	public int EditGridSize { get; set; }

	public bool EditSnapToGrid { get; set; }

	public bool EditPhysicsRunning { get; set; }

	public EditSnapMode EditSnapMode { get; set; }

	public List<ObjectData> EditPreviewObjects { get; set; }

	public List<ObjectData> EditHighlightObjectsOnce { get; set; }

	public List<Tuple<ObjectData, ObjectData>> EditHighlightObjectsFixed { get; set; }

	public List<EditHistoryItemObject> EditPreviewObjectsHistoryEntries { get; set; }

	public List<Microsoft.Xna.Framework.Vector2> EditOffsetPositions { get; set; }

	public List<ObjectData> EditSelectedObjects { get; set; }

	public SelectionArea EditSelectionArea { get; set; }

	public bool EditMapFileIsCorrupt { get; set; }

	public EditorCurors EditCursor { get; set; }

	public Microsoft.Xna.Framework.Vector2 EditMouseOffset { get; set; }

	public bool EditChangesMade { get; set; }

	public bool MapPartsEnabled => MapType == MapType.Campaign;

	public MapType MapType => ObjectWorldData.MapType;

	public int MapTotalPlayers => ObjectWorldData.MapTotalPlayers;

	public bool MapAutoFillWithBots => ObjectWorldData.MapAutoFillWithBots;

	public string MapName => ObjectWorldData.MapName;

	public string MapPartName => ObjectWorldData.MapPartName;

	public bool MapPartSelectable => ObjectWorldData.MapPartSelectable;

	public bool MapIsTemplate => ObjectWorldData.MapIsTemplate;

	public bool MapEditLock => ObjectWorldData.MapEditLock;

	public string MapPublishExternalID
	{
		get
		{
			return ObjectWorldData.MapPublishExternalID;
		}
		set
		{
			ObjectWorldData.MapPublishExternalID = value;
		}
	}

	public string MapDescription
	{
		get
		{
			return ObjectWorldData.MapDescription;
		}
		set
		{
			ObjectWorldData.MapDescription = value;
		}
	}

	public string MapTags
	{
		get
		{
			return ObjectWorldData.MapTags;
		}
		set
		{
			ObjectWorldData.MapTags = value;
		}
	}

	public Queue<PlayerAIPackage> PlayerAIPackages { get; set; }

	public Area WorldCameraSourceArea { get; set; }

	public Area WorldCameraSafeArea { get; set; }

	public Area WorldCameraMaxArea { get; set; }

	public Area ActiveCameraSafeArea
	{
		get
		{
			if (m_activeCameraSafeArea == null)
			{
				return WorldCameraSafeArea;
			}
			return m_activeCameraSafeArea;
		}
	}

	public Area ActiveCameraMaxArea
	{
		get
		{
			if (m_activeCameraMaxArea == null)
			{
				return WorldCameraMaxArea;
			}
			return m_activeCameraMaxArea;
		}
	}

	public int ActiveCameraAreaID { get; set; }

	public int InspectCameraTarget { get; set; }

	public int LastInspectCameraTarget { get; set; }

	public List<CameraFocusPoint> CameraFocusPoints { get; set; }

	public Player SpectatingPlayer
	{
		get
		{
			return m_spectatingPlayer;
		}
		set
		{
			if (m_spectatingPlayer != value)
			{
				m_spectatingPlayer = value;
				if (m_spectatingPlayer != null)
				{
					m_currentlySpectatingText = m_spectatingText + ": " + m_spectatingPlayer.Name;
					m_currentlySpectatingTextSize = Constants.MeasureString(Constants.Font1, m_currentlySpectatingText);
					m_currentlySpectatingTextPosition = new Microsoft.Xna.Framework.Vector2(GameSFD.GAME_WIDTH2f - m_currentlySpectatingTextSize.X * 0.5f, GameSFD.GAME_HEIGHT2f * 1.54f);
				}
			}
		}
	}

	public ContentManager m_content => GameSFD.Handle.Content;

	public SpriteBatch m_spriteBatch => GameSFD.Handle.m_spriteBatch;

	public GraphicsDevice m_graphicsDevice => GameSFD.Handle.GraphicsDeviceManager.GraphicsDevice;

	public GameInfo GameInfo { get; set; }

	public int GameNumber { get; set; }

	public bool MuteSounds { get; set; }

	public Dictionary<string, int> CustomIDTableLookup { get; set; }

	public GameEffects GameEffects { get; set; }

	public SFD.Weather.Weather Weather { get; set; }

	public WeaponSpawnManagerOLD WeaponSpawnManagerOLD { get; set; }

	public WeaponSpawnManager WeaponSpawnManager { get; set; }

	public Dictionary<int, Player> PlayersLookup { get; set; }

	public List<Player> Players { get; set; }

	public List<ObjectData> InfoObjects { get; set; }

	public List<ObjectData> BotSearchItems { get; set; }

	public bool UsingExtendedGameSlots => GameInfo?.ExtendedSlots ?? false;

	public List<ObjectPlayerSpawnMarker> PlayerSpawnMarkers { get; set; }

	public Dictionary<int, ObjectData> DynamicObjects { get; set; }

	public Dictionary<int, BodyData> DynamicBodies { get; set; }

	public List<BodyData> BodiesToSync { get; set; }

	public Dictionary<int, ObjectData> StaticObjects { get; set; }

	public Dictionary<int, BodyData> StaticBodies { get; set; }

	public SFDRenderCategories<ObjectData> RenderCategories { get; set; }

	public IDCounter IDCounter { get; set; }

	public bool EditReadOnly { get; set; }

	public bool InLoading { get; set; }

	public float ElapsedTotalGameTime { get; set; }

	public float ElapsedTotalRealTime { get; set; }

	public Dictionary<string, TileAnimation> SyncedTileAnimations { get; set; }

	public World GetActiveWorld => b2_world_active;

	public World GetBackgroundWorld => b2_world_background;

	public GameOwnerEnum GameOwner { get; set; }

	public SlowmotionHandler SlowmotionHandler { get; set; }

	public bool EditMode { get; set; }

	public bool IsExtensionScript { get; set; }

	public bool EditTestMode => m_game.CurrentState == State.EditorTestRun;

	public ObjectProperties PropertiesWorld { get; set; }

	public List<Player> BringPlayerToFront { get; set; }

	public ObjectWorld ObjectWorldData { get; set; }

	public int BoundsWorldBottom { get; set; }

	public string WorldID { get; set; }

	public int WorldNr { get; set; }

	public GameWorldScriptBridge ScriptBridge { get; set; }

	public Stack<SandboxInstance> CallingScriptInstance { get; set; }

	public bool CurrentActiveScriptIsExtension => !string.IsNullOrEmpty(CurrentActiveScriptInstanceUniqueID);

	public string CurrentActiveScriptInstanceUniqueID
	{
		get
		{
			if (CallingScriptInstance.Count == 0)
			{
				return "";
			}
			return CallingScriptInstance.Peek().UniqueScriptInstanceID;
		}
	}

	public string CurrentActiveScriptInstanceID
	{
		get
		{
			if (CallingScriptInstance.Count == 0)
			{
				return "";
			}
			return CallingScriptInstance.Peek().ScriptInstanceID;
		}
	}

	public bool IgnoreTimers { get; set; }

	public SFD.PathGrid.PathGrid PathGrid { get; set; }

	public SandboxInstance DefaultScript { get; set; }

	public bool AllowDisposeOfScripts { get; set; }

	public List<ObjectData> TriggerMineObjectsToKeepTrackOf { get; set; }

	public Dictionary<string, SandboxInstance> ExtensionScripts { get; set; }

	public Dictionary<string, string> ExtensionScriptsUniqueID { get; set; }

	public HashSet<int> DisposedObjectIDs { get; set; }

	public bool IsDisposed => m_disposed;

	public CameraMode WorkingCameraMode { get; set; }

	public bool FirstUpdateIsRun => m_firstUpdateIsRun;

	public double m_drawingBox2DSimulationTimestepOver
	{
		get
		{
			return m_drawingBox2DSimulationTimestepOver_property;
		}
		set
		{
			m_drawingBox2DSimulationTimestepOver_property = value;
			DrawingBox2DSimulationTimestepOver = (float)value * SlowmotionHandler.SlowmotionModifier;
		}
	}

	public bool GameOverSignalSent => m_gameOverSignalSent;

	public PlayingUserMode PlayingUsersVersusMode { get; set; }

	public bool AutoVictoryConditionEnabled { get; set; }

	public bool AutoScoreConditionEnabled { get; set; }

	public GameOverResultData GameOverData { get; set; }

	public DifficultyLevel SelectedDifficultyLevel
	{
		get
		{
			if (MapType == MapType.Campaign)
			{
				if (GameInfo.DifficultyLevel != DifficultyLevel.None)
				{
					return GameInfo.DifficultyLevel;
				}
				return DifficultyLevel.Normal;
			}
			return DifficultyLevel.None;
		}
	}

	public float CurrentDifficulty
	{
		get
		{
			switch (MapType)
			{
			default:
				return 0f;
			case MapType.Versus:
				return 1f;
			case MapType.Custom:
				return 1f;
			case MapType.Campaign:
				return SelectedDifficultyLevel switch
				{
					DifficultyLevel.Easy => 0.25f, 
					DifficultyLevel.Normal => 0.5f, 
					DifficultyLevel.Hard => 0.75f, 
					DifficultyLevel.Expert => 1f, 
					_ => 1f, 
				};
			case MapType.Survival:
			{
				int num = MapTotalPlayers;
				if (num <= 0)
				{
					num = 8;
				}
				return (float)GameInfo.PlayingGameUserCount / (float)num;
			}
			case MapType.Challenge:
				return 1f;
			}
		}
	}

	public Team GUI_TeamDisplay_LocalGameUserTeam { get; set; }

	public int GUI_TeamDisplay_LocalGameUserIdentifier { get; set; }

	public int CurrentExplosionCount => m_currentExplosionCount;

	public FireGrid FireGrid { get; set; }

	public Dictionary<ushort, GroupInfo> GroupInfo { get; set; }

	public Dictionary<ushort, GroupSpawnInfo> GroupSpawnInfo { get; set; }

	public ushort EditGroupID { get; set; }

	public bool StartupSequenceOver => ElapsedTotalRealTime >= StartupKeyInputDisabledTime;

	public float StartupKeyInputDisabledTime
	{
		get
		{
			if (ObjectWorldData.StartupSequenceEnabled)
			{
				return 2250f;
			}
			return 1000f;
		}
	}

	public float StartupSpawnFrenzyDelayTime => StartupKeyInputDisabledTime;

	public bool ClearLocalPlayerVirtualInput { get; set; }

	public byte SetLocalPlayerVirtualInput { get; set; }

	public List<ObjectData> WaterZones { get; set; }

	public List<ObjectStreetsweeper> Streetsweepers { get; set; }

	public List<ObjectOnPlayerDeathTrigger> OnPlayerDeathTriggers { get; set; }

	public List<ObjectOnPlayerDamageTrigger> OnPlayerDamageTriggers { get; set; }

	public List<ObjectOnGameOverTrigger> OnGameOverTriggers { get; set; }

	public NewObjectCollection NewObjectsCollection { get; set; }

	public List<ObjectData> CheckActivateOnStartupObjects { get; set; }

	public List<ObjectData> MissileUpdateObjects { get; set; }

	public List<ObjectData> ObjectCleanUpdates { get; set; }

	public List<ObjectData> ObjectColorUpdates { get; set; }

	public List<ObjectData> ObjectUpdateCycleUpdates { get; set; }

	public List<ObjectData> ObjectUpdateCycleUpdatesToRemove { get; set; }

	public List<ObjectData> ObjectUpdateCycleUpdatesToAdd { get; set; }

	public List<GameWorldPortal> Portals { get; set; }

	public List<ObjectData> PortalsObjectsToKeepTrackOf { get; set; }

	public List<BodyData> ObjectSyncedPositionUpdates { get; set; }

	public List<BodyData> ObjectForcedPositionUpdates { get; set; }

	public Dictionary<Body, ObjectPositionUpdateInfo> ObjectPositionUpdates { get; set; }

	public List<Projectile> Projectiles { get; set; }

	public List<Projectile> NewProjectiles { get; set; }

	public List<int> OldProjectiles { get; set; }

	public List<Projectile> RemovedProjectiles { get; set; }

	public float ProjectileUpdateModifier
	{
		get
		{
			if (SlowmotionHandler.SlowmotionModifier < 1f)
			{
				float num = 1f - SlowmotionHandler.SlowmotionModifier;
				num *= 1.15f;
				return Math.Max(1f - num, 0.1f);
			}
			return SlowmotionHandler.SlowmotionModifier;
		}
	}

	public bool IsCorrupted { get; set; }

	public bool ScriptCallbackExists
	{
		get
		{
			if (m_scriptRegisteredUpdateCallbacks != null)
			{
				return m_scriptRegisteredUpdateCallbacks.Count > 0;
			}
			return false;
		}
	}

	public Dictionary<string, LocalStorage> SessionStorageEntries
	{
		get
		{
			return m_sessionStorageEntries;
		}
		set
		{
			m_sessionStorageEntries = value;
		}
	}

	public Dictionary<string, LocalStorage> PermanentStorageEntries
	{
		get
		{
			return m_permanentStorageEntries;
		}
		set
		{
			m_permanentStorageEntries = value;
		}
	}

	public Dictionary<string, LocalStorage> SharedStorageEntries
	{
		get
		{
			return m_sharedStorageEntries;
		}
		set
		{
			m_sharedStorageEntries = value;
		}
	}

	public Dictionary<string, string> SessionDataEntries
	{
		get
		{
			return m_sessionDataEntries;
		}
		set
		{
			m_sessionDataEntries = value;
		}
	}

	public bool ScriptCallbackExists_Update { get; set; }

	public bool ScriptCallbackExists_PlayerCreated { get; set; }

	public bool ScriptCallbackExists_PlayerDamage { get; set; }

	public bool ScriptCallbackExists_PlayerDeath { get; set; }

	public bool ScriptCallbackExists_ProjectileHit { get; set; }

	public bool ScriptCallbackExists_ProjectileCreated { get; set; }

	public bool ScriptCallbackExists_ExplosionHit { get; set; }

	public bool ScriptCallbackExists_ObjectDamage { get; set; }

	public bool ScriptCallbackExists_ObjectTerminated { get; set; }

	public bool ScriptCallbackExists_ObjectCreated { get; set; }

	public bool ScriptCallbackExists_UserMessage { get; set; }

	public bool ScriptCallbackExists_PlayerMeleeAction { get; set; }

	public bool ScriptCallbackExists_PlayerWeaponRemovedAction { get; set; }

	public bool ScriptCallbackExists_PlayerWeaponAddedAction { get; set; }

	public bool ScriptCallbackExists_PlayerKeyInput { get; set; }

	public bool ScriptCallbackExists_UserJoin { get; set; }

	public bool ScriptCallbackExists_UserLeave { get; set; }

	public GameTimer PrimaryTimer
	{
		get
		{
			if (m_suddenDeathTimer != null && !m_suddenDeathTimer.IsOver)
			{
				return m_suddenDeathTimer;
			}
			if (m_mainTimer != null && !m_mainTimer.IsOver)
			{
				return m_mainTimer;
			}
			return null;
		}
	}

	public bool SuddenDeathActive => m_suddenDeathTimer != null;

	public List<ObjectPropertyInstance> ObjectPropertyValuesToSend { get; set; }

	public void EnableEditMode()
	{
		EditMode = true;
	}

	public void SetIsExtensionScript()
	{
		IsExtensionScript = true;
	}

	public void EditSetSelectionFilter(string newFilter)
	{
		m_editSelectionFitler.Clear();
		string[] array = newFilter.Split(new char[1] { '|' });
		try
		{
			string[] array2 = array;
			int num = 0;
			while (true)
			{
				if (num >= array2.Length)
				{
					return;
				}
				string text = array2[num];
				if (!string.IsNullOrEmpty(text))
				{
					string[] array3 = text.Split(new char[1] { '=' });
					if (array3.Length != 2)
					{
						break;
					}
					switch (array3[0].ToUpperInvariant())
					{
					case "EXCLUDECATEGORIES":
					{
						string[] array4 = array3[1].ToUpperInvariant().Split(new char[1] { ',' });
						foreach (string value in array4)
						{
							int item3 = (int)(Category.TYPE)Enum.Parse(typeof(Category.TYPE), value, ignoreCase: true);
							if (!m_editSelectionFitler.ExcludedCategoryTypes.Contains(item3))
							{
								m_editSelectionFitler.ExcludedCategoryTypes.Add(item3);
							}
						}
						break;
					}
					case "EXCLUDEEDITORTILES":
						m_editSelectionFitler.ExcludeEditorTiles = array3[1].ToUpperInvariant() == "TRUE";
						break;
					case "SELFTARGET":
						m_editSelectionFitler.SelfTarget = int.Parse(array3[1]);
						break;
					case "ADDITIONALTARGETS":
					{
						string[] array4 = array3[1].ToUpperInvariant().Split(new char[1] { ',' });
						foreach (string item2 in array4)
						{
							if (!m_editSelectionFitler.AdditionalTargets.Contains(item2))
							{
								m_editSelectionFitler.AdditionalTargets.Add(item2);
							}
						}
						break;
					}
					case "TARGETS":
					{
						string[] array4 = array3[1].ToUpperInvariant().Split(new char[1] { ',' });
						foreach (string item in array4)
						{
							if (!m_editSelectionFitler.Targets.Contains(item))
							{
								m_editSelectionFitler.Targets.Add(item);
							}
						}
						break;
					}
					case "TARGETSELF":
						m_editSelectionFitler.TargetSelf = array3[1].ToUpperInvariant() == "TRUE";
						break;
					}
				}
				num++;
			}
			throw new ArgumentException($"Filter '{newFilter}' is invalid - could not be parsed");
		}
		catch
		{
			throw new ArgumentException($"Filter '{newFilter}' is invalid - could not be parsed");
		}
	}

	public void EditTargetObjectData(ObjectPropertyInstance opi)
	{
		List<ObjectPropertyInstance> list = new List<ObjectPropertyInstance>();
		list.Add(opi);
		EditTargetObjectData(list);
	}

	public void EditTargetObjectData(List<ObjectPropertyInstance> opis)
	{
		if (opis != null && opis.Count != 0)
		{
			m_editTargetObjectPropertyInstance = opis[0];
			m_editTargetObjectPropertyInstances = opis;
			EditSetSelectionFilter($"{m_editTargetObjectPropertyInstance.Base.Filter}|selfTarget={m_editTargetObjectPropertyInstance.ObjectOwner.ObjectID}");
			if (m_editTargetObjectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
			{
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.UpdateActionText, LanguageHelper.GetText("mapEditor.selectTarget"));
			}
			else if (m_editTargetObjectPropertyInstance.Base.PropertyClass == ObjectPropertyClass.TargetObjectDataMultiple)
			{
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.UpdateActionText, LanguageHelper.GetText("mapEditor.selectTargets"));
			}
		}
	}

	public void EditCancelCurrentAction()
	{
		if (m_editTargetObjectPropertyInstance != null)
		{
			EditCancelTargetObjectPropertyInstance();
		}
	}

	public void EditCancelTargetObjectPropertyInstance()
	{
		m_editTargetObjectPropertyInstance = null;
		m_editTargetObjectPropertyInstances = null;
		EditSetSelectionFilter("");
		m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.UpdateActionText, "");
		EditSetMapEditorPropertiesWindow();
	}

	public void EditSetPhysicsOff()
	{
		if (EditPhysicsRunning)
		{
			EditHistoryClearAll();
			EditPhysicsRunning = false;
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.UpdateGUIButtons, EditSnapToGrid, EditDrawGrid, EditDrawCenter, EditDrawWorldZones, EditDrawPathGrid, EditPhysicsRunning);
		}
	}

	public void EditUpdateCursor(Microsoft.Xna.Framework.Vector2 worldPosition, bool mouseInsideArea)
	{
		if (!mouseInsideArea)
		{
			EditCursor = EditorCurors.None;
		}
		else if (m_editTargetObjectPropertyInstance != null)
		{
			EditCursor = EditorCurors.Cross;
		}
		else if (m_editRotationInAction)
		{
			EditCursor = EditorCurors.Rotate;
		}
		else if (EditPreviewObjects.Count > 0)
		{
			EditCursor = EditorCurors.DragDrop;
		}
		else if (m_editLeftMoveAction == EditLeftMoveAction.Move)
		{
			EditCursor = EditorCurors.Move;
		}
		else if ((!EditPhysicsRunning && EditSelectedObjects.Count == 1 && EditCheckTouchSizeable(worldPosition, out m_sizeableOut)) || m_editLeftMoveAction == EditLeftMoveAction.Resize)
		{
			if (m_sizeableOut != Tile.SIZEABLE.N)
			{
				m_sizeableOutLastNonN = m_sizeableOut;
			}
			float num = EditSelectedObjects[0].GetAngle();
			switch (m_sizeableOutLastNonN)
			{
			case Tile.SIZEABLE.V:
				num -= (float)Math.PI / 2f;
				break;
			case Tile.SIZEABLE.H:
				num -= 0f;
				break;
			case Tile.SIZEABLE.D:
				num -= (float)Math.PI / 4f;
				break;
			}
			if (m_sizeableCursorsDirections == null)
			{
				m_sizeableCursorsDirections = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>[8];
				m_sizeableCursorsDirections[0] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-0f), EditorCurors.SizeWE);
				m_sizeableCursorsDirections[1] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-(float)Math.PI / 4f), EditorCurors.SizeNWSE);
				m_sizeableCursorsDirections[2] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-(float)Math.PI / 2f), EditorCurors.SizeNS);
				m_sizeableCursorsDirections[3] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge((float)Math.PI * -3f / 4f), EditorCurors.SizeNESW);
				m_sizeableCursorsDirections[4] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-(float)Math.PI), EditorCurors.SizeWE);
				m_sizeableCursorsDirections[5] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-3.926991f), EditorCurors.SizeNWSE);
				m_sizeableCursorsDirections[6] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-4.712389f), EditorCurors.SizeNS);
				m_sizeableCursorsDirections[7] = new ItemContainer<Microsoft.Xna.Framework.Vector2, EditorCurors>(AngleUtil.Vector2FromAnlge(-5.4977875f), EditorCurors.SizeNESW);
			}
			Microsoft.Xna.Framework.Vector2 value = AngleUtil.Vector2FromAnlge(num);
			int num2 = -1;
			float num3 = 999f;
			float num4 = 1f;
			num3 = Math.Abs(Microsoft.Xna.Framework.Vector2.Dot(m_sizeableCursorsDirections[0].Item1, value) - 1f);
			num2 = 0;
			for (int i = 1; i < 8; i++)
			{
				num4 = Math.Abs(Microsoft.Xna.Framework.Vector2.Dot(m_sizeableCursorsDirections[i].Item1, value) - 1f);
				if (num4 < num3)
				{
					num3 = num4;
					num2 = i;
				}
			}
			EditCursor = m_sizeableCursorsDirections[num2].Item2;
		}
		else if (!EditPhysicsRunning && EditSelectedObjects.Count > 0 && EditCheckTouch(EditSelectedObjects, worldPosition) != null)
		{
			EditCursor = EditorCurors.Move;
		}
		else
		{
			EditCursor = EditorCurors.None;
		}
	}

	public ObjectData EditCheckTouch(List<ObjectData> objects, Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		int num = objects.Count - 1;
		while (true)
		{
			if (num >= 0)
			{
				if (EditCheckTouch(objects[num], worldPosition))
				{
					break;
				}
				num--;
				continue;
			}
			return null;
		}
		return objects[num];
	}

	public Tile.SIZEABLE EditCheckTouchSizeable(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (EditSelectedObjects.Count == 1)
		{
			ObjectData objectData = EditSelectedObjects[0];
			Tile.SIZEABLE[] obj = new Tile.SIZEABLE[3]
			{
				Tile.SIZEABLE.D,
				Tile.SIZEABLE.H,
				Tile.SIZEABLE.V
			};
			Microsoft.Xna.Framework.Vector2 result = Microsoft.Xna.Framework.Vector2.Zero;
			Tile.SIZEABLE[] array = obj;
			foreach (Tile.SIZEABLE sIZEABLE in array)
			{
				if (objectData.GetSizeablePosition(sIZEABLE, out result) && !(Microsoft.Xna.Framework.Vector2.Distance(worldPosition, result) >= 2f))
				{
					return sIZEABLE;
				}
			}
		}
		return Tile.SIZEABLE.N;
	}

	public bool EditCheckTouchSizeable(Microsoft.Xna.Framework.Vector2 worldPosition, out Tile.SIZEABLE tileSizeable)
	{
		if (EditSelectedObjects.Count == 1)
		{
			ObjectData objectData = EditSelectedObjects[0];
			Tile.SIZEABLE[] obj = new Tile.SIZEABLE[3]
			{
				Tile.SIZEABLE.D,
				Tile.SIZEABLE.H,
				Tile.SIZEABLE.V
			};
			Microsoft.Xna.Framework.Vector2 result = Microsoft.Xna.Framework.Vector2.Zero;
			Tile.SIZEABLE[] array = obj;
			foreach (Tile.SIZEABLE sIZEABLE in array)
			{
				if (objectData.GetSizeablePosition(sIZEABLE, out result) && !(Microsoft.Xna.Framework.Vector2.Distance(worldPosition, result) >= 2f))
				{
					tileSizeable = sIZEABLE;
					return true;
				}
			}
		}
		tileSizeable = Tile.SIZEABLE.N;
		return false;
	}

	public bool EditCheckTouch(ObjectData od, Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (od == null)
		{
			return false;
		}
		if (EditGroupID > 0 && od.GroupID != EditGroupID)
		{
			return false;
		}
		return od.EditCheckTouch(worldPosition);
	}

	public bool EditCheckTouch(ObjectData od, SelectionArea area)
	{
		if (od == null)
		{
			return false;
		}
		if (EditGroupID > 0 && od.GroupID != EditGroupID)
		{
			return false;
		}
		return od.EditCheckTouch(area);
	}

	public bool EditCheckObjectSelectable(ObjectData objectData)
	{
		if (objectData.LocalRenderLayer == -1)
		{
			return false;
		}
		Layer<ObjectData> layer = RenderCategories[objectData.Tile.DrawCategory].GetLayer(objectData.LocalRenderLayer);
		if (!layer.IsLocked)
		{
			return layer.IsVisible;
		}
		return false;
	}

	public void EditClickButton(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		List<ObjectData> triggers = null;
		AABB.Create(out var aabb, Converter.WorldToBox2D(worldPosition), 0.16f);
		GetActiveWorld.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture != null)
			{
				ObjectData objectData = ObjectData.Read(fixture);
				if (objectData is ObjectActivateTrigger && objectData.Activateable && EditCheckTouch(objectData, worldPosition))
				{
					if (triggers == null)
					{
						triggers = new List<ObjectData>();
					}
					triggers.Add(objectData);
				}
			}
			return true;
		}, ref aabb);
		if (triggers == null)
		{
			return;
		}
		foreach (ObjectData item in triggers)
		{
			if (!item.IsDisposed)
			{
				item.Activate(null);
			}
		}
	}

	public Microsoft.Xna.Framework.Vector2 EditGetCenterOfSelection()
	{
		if (EditSelectedObjects.Count > 0)
		{
			Microsoft.Xna.Framework.Vector2 box2DCenterPosition = EditSelectedObjects[0].GetBox2DCenterPosition();
			if (EditSelectedObjects.Count == 1)
			{
				return box2DCenterPosition;
			}
			for (int i = 1; i < EditSelectedObjects.Count; i++)
			{
				box2DCenterPosition += EditSelectedObjects[i].GetBox2DCenterPosition();
			}
			return box2DCenterPosition * (1f / (float)EditSelectedObjects.Count);
		}
		return Microsoft.Xna.Framework.Vector2.Zero;
	}

	public Microsoft.Xna.Framework.Vector2 EditGetActiveCenterOfSelection()
	{
		if (m_editRotationInAction)
		{
			return m_editRotationPositionCenter;
		}
		return EditGetCenterOfSelection();
	}

	public Microsoft.Xna.Framework.Vector2 EditSnapPositionToGrid(Microsoft.Xna.Framework.Vector2 worldPosition, ObjectData objectData, bool snapX, bool snapY)
	{
		int gridSize = EditGridSize;
		if (!DoSnapToGrid())
		{
			gridSize = 1;
		}
		return EditSnapPositionToGrid(worldPosition, objectData, snapX, snapY, gridSize);
	}

	public Microsoft.Xna.Framework.Vector2 EditSnapPositionToGrid(Microsoft.Xna.Framework.Vector2 worldPosition, ObjectData objectData, bool snapX, bool snapY, int gridSize)
	{
		Microsoft.Xna.Framework.Vector2 vector = worldPosition;
		FixedArray2<int> textureSize = objectData.GetTextureSize();
		float num = textureSize[0];
		float num2 = textureSize[1];
		if (EditSnapMode == EditSnapMode.TopLeft)
		{
			float rotation = 0f;
			if (objectData.Body != null)
			{
				rotation = objectData.GetAngle();
			}
			float x = (num - (float)gridSize) / 2f;
			float y = (num2 - (float)gridSize) / 2f;
			Microsoft.Xna.Framework.Vector2 position = new Microsoft.Xna.Framework.Vector2(x, y);
			SFDMath.RotatePosition(ref position, rotation, out position);
			float x2 = (float)gridSize / 2f;
			float y2 = (float)gridSize / 2f;
			Microsoft.Xna.Framework.Vector2 position2 = new Microsoft.Xna.Framework.Vector2(x2, y2);
			SFDMath.RotatePosition(ref position2, rotation, out position2);
			worldPosition.X -= position2.X;
			worldPosition.Y += position2.Y;
			worldPosition.X -= position.X;
			worldPosition.Y += position.Y;
			worldPosition.X = SFDMath.Round((int)Math.Round(worldPosition.X), gridSize);
			worldPosition.Y = SFDMath.Round((int)Math.Round(worldPosition.Y), gridSize);
			if (EditSnapMode == EditSnapMode.TopLeft)
			{
				float x3 = num / 2f;
				float y3 = num2 / 2f;
				Microsoft.Xna.Framework.Vector2 position3 = new Microsoft.Xna.Framework.Vector2(x3, y3);
				if (objectData.Body != null)
				{
					SFDMath.RotatePosition(ref position3, objectData.GetAngle(), out position3);
				}
				worldPosition.X += position3.X;
				worldPosition.Y -= position3.Y;
				if (!snapX)
				{
					worldPosition.X = vector.X;
				}
				if (!snapY)
				{
					worldPosition.Y = vector.Y;
				}
				return worldPosition;
			}
			throw new NotImplementedException("SnapMode " + EditSnapMode.ToString() + " not supported yet.");
		}
		throw new NotImplementedException("SnapMode " + EditSnapMode.ToString() + " not supported yet.");
	}

	public bool IsControlPressed()
	{
		if (GameSFD.Handle.CurrentState == State.Editor && (GameSFD.Handle.GetRunningState() as StateEditor).IsControlPressed())
		{
			return true;
		}
		return SFD.Input.Keyboard.IsCtrlDown();
	}

	public bool IsShiftPressed()
	{
		if (GameSFD.Handle.CurrentState == State.Editor && (GameSFD.Handle.GetRunningState() as StateEditor).IsShiftPressed())
		{
			return true;
		}
		return SFD.Input.Keyboard.IsShiftDown();
	}

	public bool IsAltPressed()
	{
		if (GameSFD.Handle.CurrentState == State.Editor && (GameSFD.Handle.GetRunningState() as StateEditor).IsAltPressed())
		{
			return true;
		}
		return SFD.Input.Keyboard.IsAltDown();
	}

	public bool IsModifierPressed()
	{
		if (GameSFD.Handle.CurrentState == State.Editor && (GameSFD.Handle.GetRunningState() as StateEditor).IsModifierPressed())
		{
			return true;
		}
		if (!IsControlPressed() && !IsShiftPressed())
		{
			return IsAltPressed();
		}
		return true;
	}

	public bool DoSnapToGrid()
	{
		bool flag = EditSnapToGrid;
		if (IsShiftPressed())
		{
			flag = !flag;
		}
		return flag;
	}

	public void EditHistoryClearAll()
	{
		m_editHistoryActions.Clear();
		m_editHistoryActionsUndone.Clear();
	}

	public void EditNotAllowed(bool isExtensionScript = false)
	{
		if (!m_editNotAllowedRunning)
		{
			m_editNotAllowedRunning = true;
			Update(Constants.PREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE, Constants.PREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE, isLast: true, isFirst: true);
			EditHistoryRevertAllActions(isAutomaticRevert: true);
			EditChangesMade = false;
			EditSelectedObjects.Clear();
			EditSetChangesUnmade();
			m_editHistoryActionsUndone.Clear();
			EditNotAllowedShowMessage(isExtensionScript);
			m_editNotAllowedRunning = false;
		}
	}

	public void EditNotAllowedShowMessage(bool isExtensionScript = false)
	{
		MessageBox.Show(LanguageHelper.GetText(isExtensionScript ? "mapEditor.messages.onlyscriptcanbeeditedforextensionscripts" : "mapEditor.messages.editnotallowedwhilelocked"), LanguageHelper.GetText("mapEditor.messages.mapislocked"), MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
	}

	public void EditHistoryGroupBegin()
	{
		if (m_editHistoryGroupLevel == 0)
		{
			m_editHistoryGroupItems = new List<EditHistoryItemBase>();
		}
		m_editHistoryGroupLevel++;
	}

	public void EditHistoryActionsCap()
	{
		if (m_editHistoryActions.Count >= 510)
		{
			m_editHistoryActions.RemoveAt(m_editHistoryActions.Count - 1);
		}
	}

	public void EditHistoryGroupEnd()
	{
		m_editHistoryGroupLevel--;
		if (m_editHistoryGroupLevel == 0 && m_editHistoryGroupItems.Count > 0)
		{
			EditHistoryActionsCap();
			m_editHistoryActions.Insert(0, m_editHistoryGroupItems);
			m_editHistoryActionsUndone.Clear();
			m_editHistoryGroupItems = new List<EditHistoryItemBase>();
			if (!EditReadOnly && !IsExtensionScript)
			{
				EditSetChangesMade();
			}
			else
			{
				EditNotAllowed(IsExtensionScript);
			}
		}
	}

	public void EditHistoryAddEntry(EditHistoryItemBase editHistoryItem)
	{
		if (m_editHistoryGroupLevel > 0)
		{
			m_editHistoryGroupItems.Add(editHistoryItem);
		}
		else
		{
			EditHistoryActionsCap();
			List<EditHistoryItemBase> list = new List<EditHistoryItemBase>(1);
			list.Add(editHistoryItem);
			m_editHistoryActions.Insert(0, list);
			if (EditReadOnly || IsExtensionScript)
			{
				EditNotAllowed(IsExtensionScript);
				return;
			}
			EditSetChangesMade();
		}
		m_editHistoryActionsUndone.Clear();
	}

	public void EditSetChangesMade()
	{
		if (!EditChangesMade)
		{
			EditChangesMade = true;
			string mapName = ObjectWorldData.MapName;
			ObjectWorldData.MapName = mapName + " ";
			ObjectWorldData.MapName = mapName;
		}
	}

	public void EditSetChangesUnmade()
	{
		EditChangesMade = false;
		string mapName = ObjectWorldData.MapName;
		ObjectWorldData.MapName = mapName + " ";
		ObjectWorldData.MapName = mapName;
	}

	public bool EditHistoryRevertActionPossible()
	{
		if (m_editHistoryActions.Count > 0)
		{
			return EditPreviewObjects.Count <= 0;
		}
		return false;
	}

	public bool EditHistoryCanPerform()
	{
		if (m_editLeftMoveAction != EditLeftMoveAction.None)
		{
			return false;
		}
		if (m_editRotationInAction)
		{
			return false;
		}
		return true;
	}

	public void EditHistoryRevertAction()
	{
		EditHistoryRevertAction(isAutomaticRevert: false);
	}

	public void EditHistoryRevertAllActions(bool isAutomaticRevert = false)
	{
		while (m_editHistoryActions.Count > 0)
		{
			EditHistoryRevertAction(isAutomaticRevert);
		}
	}

	public void EditHistoryRevertAction(bool isAutomaticRevert)
	{
		if (!isAutomaticRevert && !EditHistoryCanPerform())
		{
			return;
		}
		try
		{
			EditCancelCurrentAction();
			HandleObjectCleanCycle();
			if (m_editHistoryActions.Count <= 0)
			{
				return;
			}
			List<EditHistoryItemBase> list = m_editHistoryActions[0];
			if (isAutomaticRevert)
			{
				EditHistoryActionInSelection(list, EditHistoryObjectAction.Deletion);
			}
			if (!isAutomaticRevert && !EditHistoryActionInSelection(list, EditHistoryObjectAction.Deletion))
			{
				return;
			}
			EditSelectedObjects.Clear();
			m_editHistoryActions.RemoveAt(0);
			for (int num = list.Count - 1; num >= 0; num--)
			{
				EditHistoryItemBase editHistoryItemBase = list[num];
				if (editHistoryItemBase.Type == EditHistoryItemBase.HistoryType.Object)
				{
					EditHistoryRevertActionProcess((EditHistoryItemObject)editHistoryItemBase);
				}
				else if (editHistoryItemBase.Type == EditHistoryItemBase.HistoryType.Layer)
				{
					EditHistoryRevertActionProcess((EditHistoryItemLayer)editHistoryItemBase);
				}
			}
			EditHistoryRemoveDuplicateSelectedObjects();
			m_editHistoryActionsUndone.Insert(0, list);
			EditAutoCloseEditGroupOnSelectionChanged();
			EditAutoSelectGroupedObjects(EditSelectedObjects);
			EditAfterSelection();
			EditSetChangesMade();
			EditEnsureLayersVisibleForSelectedObjects();
		}
		catch (Exception e)
		{
			EditHistoryError(e);
		}
	}

	public bool EditHistoryRedoActionPossible()
	{
		if (m_editHistoryActionsUndone.Count > 0)
		{
			return EditPreviewObjects.Count <= 0;
		}
		return false;
	}

	public void EditHistoryRedoAction()
	{
		if (!EditHistoryCanPerform())
		{
			return;
		}
		try
		{
			EditCancelCurrentAction();
			HandleObjectCleanCycle();
			if (m_editHistoryActionsUndone.Count <= 0)
			{
				return;
			}
			List<EditHistoryItemBase> list = m_editHistoryActionsUndone[0];
			if (!EditHistoryActionInSelection(list, EditHistoryObjectAction.Creation))
			{
				return;
			}
			EditSelectedObjects.Clear();
			m_editHistoryActionsUndone.RemoveAt(0);
			foreach (EditHistoryItemBase item in list)
			{
				if (item.Type == EditHistoryItemBase.HistoryType.Object)
				{
					EditHistoryUndoActionProcess((EditHistoryItemObject)item);
				}
				else if (item.Type == EditHistoryItemBase.HistoryType.Layer)
				{
					EditHistoryUndoActionProcess((EditHistoryItemLayer)item);
				}
			}
			EditHistoryRemoveDuplicateSelectedObjects();
			m_editHistoryActions.Insert(0, list);
			EditAutoCloseEditGroupOnSelectionChanged();
			EditAutoSelectGroupedObjects(EditSelectedObjects);
			EditAfterSelection();
			EditSetChangesMade();
			EditEnsureLayersVisibleForSelectedObjects();
		}
		catch (Exception e)
		{
			EditHistoryError(e);
		}
	}

	public void EditHistoryRemoveDuplicateSelectedObjects()
	{
		if (EditSelectedObjects.Count == 0)
		{
			return;
		}
		HashSet<ObjectData> hashSet = new HashSet<ObjectData>();
		for (int num = EditSelectedObjects.Count - 1; num >= 0; num--)
		{
			if (!hashSet.Add(EditSelectedObjects[num]))
			{
				EditSelectedObjects.RemoveAt(num);
			}
		}
	}

	public bool EditHistoryActionInSelection(List<EditHistoryItemBase> items, EditHistoryObjectAction actionToIgnore)
	{
		bool result = true;
		List<ObjectData> list = new List<ObjectData>();
		HashSet<int> hashSet = new HashSet<int>();
		foreach (EditHistoryItemBase item in items)
		{
			if (item is EditHistoryItemObject)
			{
				EditHistoryItemObject editHistoryItemObject = (EditHistoryItemObject)item;
				if (editHistoryItemObject.Action == actionToIgnore || editHistoryItemObject.MapObjectId == "WORLD" || editHistoryItemObject.MapObjectId == "WORLDLAYER")
				{
					return true;
				}
				if (hashSet.Add(editHistoryItemObject.ObjectId))
				{
					ObjectData objectDataByID = GetObjectDataByID(editHistoryItemObject.ObjectId);
					list.Add(objectDataByID);
				}
			}
		}
		EditAutoSelectGroupedObjects(list);
		bool flag = false;
		if (list.Count != EditSelectedObjects.Count)
		{
			flag = true;
		}
		else
		{
			foreach (ObjectData item2 in list)
			{
				if (!EditSelectedObjects.Contains(item2))
				{
					flag = true;
					break;
				}
			}
		}
		if (flag)
		{
			result = false;
			EditSelectedObjects.Clear();
			EditSelectedObjects.AddRange(list);
			list.Clear();
			EditAutoCloseEditGroupOnSelectionChanged();
			EditAfterSelection();
		}
		return result;
	}

	public void EditHistoryError(Exception e)
	{
		m_editHistoryActionsUndone.Clear();
		m_editHistoryActions.Clear();
		EditSelectedObjects.Clear();
		EditAfterSelection();
		m_editHistoryGroupLevel = 0;
		m_editHistoryGroupItems = new List<EditHistoryItemBase>();
		string reportMsg = e.ToString() + "\r\nEdit history failed for unknown reason.\r\nHistory has been cleared to avoid crash.";
		Reports.Create("sfd_editor_crash", reportMsg);
		MessageBox.Show(e.ToString() + "\r\nEdit history failed for unknown reason.\r\nHistory has been cleared to avoid crash.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
	}

	public void EditEnsureLayersVisibleForSelectedObjects()
	{
		Dictionary<int, List<int>> dictionary = new Dictionary<int, List<int>>();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (!dictionary.ContainsKey(editSelectedObject.LocalDrawCategory))
			{
				dictionary.Add(editSelectedObject.LocalDrawCategory, new List<int>());
			}
			if (!dictionary[editSelectedObject.LocalDrawCategory].Contains(editSelectedObject.LocalRenderLayer))
			{
				dictionary[editSelectedObject.LocalDrawCategory].Add(editSelectedObject.LocalRenderLayer);
			}
		}
		foreach (int key in dictionary.Keys)
		{
			foreach (int item in dictionary[key])
			{
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerShow, key, item, RenderCategories.Categories[key].GetLayer(0));
			}
		}
		dictionary.Clear();
		dictionary = null;
	}

	public void EditHistoryRevertActionProcess(EditHistoryItemObject item)
	{
		switch (item.Action)
		{
		case EditHistoryObjectAction.Movement:
		{
			float angle = item.DataBefore.Angle;
			Microsoft.Xna.Framework.Vector2 position = item.DataBefore.Position;
			ObjectData objectDataByID6 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID6 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to move (1)");
			}
			objectDataByID6.Body.SetTransform(position, angle);
			EditSelectedObjects.Add(objectDataByID6);
			break;
		}
		case EditHistoryObjectAction.Deletion:
		{
			Microsoft.Xna.Framework.Vector2 position3 = item.DataBefore.Position;
			position3 = Converter.ConvertBox2DToWorld(position3);
			float angle3 = item.DataBefore.Angle;
			string[] colorNames = item.DataBefore.ColorNames;
			object[] properties = item.DataBefore.Properties;
			short faceDirection2 = item.DataBefore.FaceDirection;
			int localLayerIndex = item.DataBefore.LocalLayerIndex;
			DisposedObjectIDs.Remove(item.ObjectId);
			ObjectData objectData = CreateObjectData(item.MapObjectId, item.CustomId, item.ObjectId);
			CreateTile(new SpawnObjectInformation(objectData, position3, angle3, faceDirection2, localLayerIndex));
			objectData.Properties.SetValues(properties);
			objectData.ApplyColors(colorNames);
			objectData.SetZOrder(item.DataBefore.ZOrder);
			objectData.SetGroupID(item.DataBefore.GroupID);
			objectData.FinalizeProperties();
			objectData.EditAfterHistoryRecreated();
			EditSelectedObjects.Add(objectData);
			break;
		}
		case EditHistoryObjectAction.Creation:
		{
			ObjectData objectDataByID8 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID8 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to delete (1)");
			}
			EditSelectedObjects.Remove(objectDataByID8);
			objectDataByID8.Remove();
			break;
		}
		case EditHistoryObjectAction.ChangeZOrder:
		{
			ObjectData objectDataByID2 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID2 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change Z order (1)");
			}
			objectDataByID2.SetZOrder(item.DataBefore.ZOrder);
			EditSelectedObjects.Add(objectDataByID2);
			break;
		}
		case EditHistoryObjectAction.PropertyValueChange:
		{
			if (item.MapObjectId == "WORLD")
			{
				PropertiesWorld.SetValues(item.DataBefore.Properties);
				break;
			}
			if (item.MapObjectId == "WORLDLAYER")
			{
				int num = (int)item.Args[0];
				int num2 = (int)item.Args[1];
				((SFDLayerTag)RenderCategories[num].GetLayer(num2).Tag).Properties.SetValues(item.DataBefore.Properties, networkSilent: true);
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, num, num2);
				break;
			}
			ObjectData objectDataByID3 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID3 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change properties (1)");
			}
			objectDataByID3.Properties.SetValues(item.DataBefore.Properties);
			EditSelectedObjects.Add(objectDataByID3);
			break;
		}
		case EditHistoryObjectAction.ColorValueChange:
		{
			ObjectData objectDataByID5 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID5 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change color (1)");
			}
			if (!ArrayUlti.ArrsEquals(item.DataBefore.ColorNames, item.DataAfter.ColorNames))
			{
				objectDataByID5.ApplyColors(item.DataBefore.ColorNames);
			}
			EditSelectedObjects.Add(objectDataByID5);
			break;
		}
		case EditHistoryObjectAction.Rotation:
		{
			float angle4 = item.DataBefore.Angle;
			Microsoft.Xna.Framework.Vector2 position4 = item.DataBefore.Position;
			ObjectData objectDataByID9 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID9 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to rotate (1)");
			}
			objectDataByID9.Body.SetTransform(position4, angle4);
			EditSelectedObjects.Add(objectDataByID9);
			break;
		}
		case EditHistoryObjectAction.Flip:
		{
			float angle2 = item.DataBefore.Angle;
			Microsoft.Xna.Framework.Vector2 position2 = item.DataBefore.Position;
			short faceDirection = item.DataBefore.FaceDirection;
			ObjectData objectDataByID7 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID7 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to rotate (1)");
			}
			objectDataByID7.EditFlipObject(faceDirection);
			objectDataByID7.Body.SetTransform(position2, angle2);
			EditSelectedObjects.Add(objectDataByID7);
			break;
		}
		case EditHistoryObjectAction.GroupChange:
		{
			ObjectData objectDataByID4 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID4 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change group (1)");
			}
			objectDataByID4.SetGroupID(item.DataBefore.GroupID);
			EditSelectedObjects.Add(objectDataByID4);
			break;
		}
		case EditHistoryObjectAction.LayerChange:
		{
			ObjectData objectDataByID = GetObjectDataByID(item.ObjectId);
			if (objectDataByID == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change layer (1)");
			}
			objectDataByID.RemoveFromRenderLayer();
			objectDataByID.AddToRenderLayer(item.DataBefore.LocalLayerIndex, item.DataBefore.LocalDrawCategory);
			objectDataByID.SetZOrder(item.DataBefore.ZOrder);
			EditSelectedObjects.Add(objectDataByID);
			break;
		}
		}
	}

	public void EditHistoryUndoActionProcess(EditHistoryItemObject item)
	{
		switch (item.Action)
		{
		case EditHistoryObjectAction.Movement:
		{
			Microsoft.Xna.Framework.Vector2 position = item.DataAfter.Position;
			float angle = item.DataAfter.Angle;
			ObjectData objectDataByID6 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID6 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to move (2)");
			}
			objectDataByID6.Body.SetTransform(position, angle);
			EditSelectedObjects.Add(objectDataByID6);
			break;
		}
		case EditHistoryObjectAction.Deletion:
		{
			ObjectData objectDataByID8 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID8 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to delete (2)");
			}
			EditSelectedObjects.Remove(objectDataByID8);
			objectDataByID8.Remove();
			break;
		}
		case EditHistoryObjectAction.Creation:
		{
			Microsoft.Xna.Framework.Vector2 position3 = item.DataAfter.Position;
			position3 = Converter.ConvertBox2DToWorld(position3);
			float angle3 = item.DataAfter.Angle;
			string[] colorNames = item.DataAfter.ColorNames;
			object[] properties = item.DataAfter.Properties;
			short faceDirection2 = item.DataAfter.FaceDirection;
			int localLayerIndex = item.DataAfter.LocalLayerIndex;
			DisposedObjectIDs.Remove(item.ObjectId);
			ObjectData objectData = CreateObjectData(item.MapObjectId, item.CustomId, item.ObjectId);
			CreateTile(new SpawnObjectInformation(objectData, position3, angle3, faceDirection2, localLayerIndex));
			objectData.Properties.SetValues(properties);
			objectData.ApplyColors(colorNames);
			objectData.SetZOrder(item.DataAfter.ZOrder);
			objectData.SetGroupID(item.DataAfter.GroupID);
			objectData.FinalizeProperties();
			objectData.EditAfterHistoryRecreated();
			EditSelectedObjects.Add(objectData);
			break;
		}
		case EditHistoryObjectAction.ChangeZOrder:
		{
			ObjectData objectDataByID2 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID2 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change Z order (2)");
			}
			objectDataByID2.SetZOrder(item.DataAfter.ZOrder);
			EditSelectedObjects.Add(objectDataByID2);
			break;
		}
		case EditHistoryObjectAction.PropertyValueChange:
		{
			if (item.MapObjectId == "WORLD")
			{
				PropertiesWorld.SetValues(item.DataAfter.Properties);
				break;
			}
			if (item.MapObjectId == "WORLDLAYER")
			{
				int num = (int)item.Args[0];
				int num2 = (int)item.Args[1];
				((SFDLayerTag)RenderCategories[num].GetLayer(num2).Tag).Properties.SetValues(item.DataAfter.Properties, networkSilent: true);
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, num, num2);
				break;
			}
			ObjectData objectDataByID3 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID3 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change properties (2)");
			}
			objectDataByID3.Properties.SetValues(item.DataAfter.Properties);
			EditSelectedObjects.Add(objectDataByID3);
			break;
		}
		case EditHistoryObjectAction.ColorValueChange:
		{
			ObjectData objectDataByID5 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID5 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change color (2)");
			}
			if (!ArrayUlti.ArrsEquals(item.DataBefore.ColorNames, item.DataAfter.ColorNames))
			{
				objectDataByID5.ApplyColors(item.DataAfter.ColorNames);
			}
			EditSelectedObjects.Add(objectDataByID5);
			break;
		}
		case EditHistoryObjectAction.Rotation:
		{
			Microsoft.Xna.Framework.Vector2 position4 = item.DataAfter.Position;
			float angle4 = item.DataAfter.Angle;
			ObjectData objectDataByID9 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID9 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to rotate (2)");
			}
			objectDataByID9.Body.SetTransform(position4, angle4);
			EditSelectedObjects.Add(objectDataByID9);
			break;
		}
		case EditHistoryObjectAction.Flip:
		{
			Microsoft.Xna.Framework.Vector2 position2 = item.DataAfter.Position;
			float angle2 = item.DataAfter.Angle;
			short faceDirection = item.DataAfter.FaceDirection;
			ObjectData objectDataByID7 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID7 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to rotate (2)");
			}
			objectDataByID7.EditFlipObject(faceDirection);
			objectDataByID7.Body.SetTransform(position2, angle2);
			EditSelectedObjects.Add(objectDataByID7);
			break;
		}
		case EditHistoryObjectAction.GroupChange:
		{
			ObjectData objectDataByID4 = GetObjectDataByID(item.ObjectId);
			if (objectDataByID4 == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change group (2)");
			}
			objectDataByID4.SetGroupID(item.DataAfter.GroupID);
			EditSelectedObjects.Add(objectDataByID4);
			break;
		}
		case EditHistoryObjectAction.LayerChange:
		{
			ObjectData objectDataByID = GetObjectDataByID(item.ObjectId);
			if (objectDataByID == null)
			{
				throw new Exception("Error Editor: Could not find object (ObjectId=" + item.ObjectId + " MapObjectId=" + item.MapObjectId + ") to change layer (2)");
			}
			objectDataByID.RemoveFromRenderLayer();
			objectDataByID.AddToRenderLayer(item.DataAfter.LocalLayerIndex, item.DataAfter.LocalDrawCategory);
			objectDataByID.SetZOrder(item.DataAfter.ZOrder);
			EditSelectedObjects.Add(objectDataByID);
			break;
		}
		}
	}

	public void EditHistoryRevertActionProcess(EditHistoryItemLayer item)
	{
		switch (item.Action)
		{
		case EditHistoryLayerAction.Rename:
		{
			Layer<ObjectData> layer = RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex);
			layer.Name = item.DataBefore.Name;
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item.CategoryIndex, item.LayerIndex, layer);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex);
			break;
		}
		case EditHistoryLayerAction.Remove:
			EditAddLayer(Category.ToName(item.DataBefore.CategoryIndex), item.DataBefore.LayerIndex, item.DataBefore.Name, addHistory: false);
			((SFDLayerTag)RenderCategories[item.DataBefore.CategoryIndex].GetLayer(item.DataBefore.LayerIndex).Tag).Properties.SetValues(item.DataBefore.Properties, networkSilent: true);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerAdd, item.CategoryIndex, item.LayerIndex, RenderCategories[item.DataBefore.CategoryIndex].GetLayer(item.DataBefore.LayerIndex));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex);
			break;
		case EditHistoryLayerAction.Copy:
			EditRemoveLayer(Category.ToName(item.CategoryIndex), item.LayerIndex + 1, EditRemoveLayerSource.History);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerRemove, item.CategoryIndex, item.LayerIndex + 1);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex + 1);
			break;
		case EditHistoryLayerAction.Add:
			EditRemoveLayer(Category.ToName(item.CategoryIndex), item.LayerIndex, EditRemoveLayerSource.History);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerRemove, item.CategoryIndex, item.LayerIndex);
			break;
		case EditHistoryLayerAction.MoveUp:
		case EditHistoryLayerAction.MoveDown:
			EditChangeLayers(Category.ToName(item.CategoryIndex), item.LayerIndex, item.LayerIndex + 1, addHistory: false);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item.CategoryIndex, item.LayerIndex, RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item.CategoryIndex, item.LayerIndex + 1, RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex + 1));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex + ((item.Action == EditHistoryLayerAction.MoveDown) ? 1 : 0));
			break;
		}
	}

	public void EditHistoryUndoActionProcess(EditHistoryItemLayer item)
	{
		switch (item.Action)
		{
		case EditHistoryLayerAction.Rename:
		{
			Layer<ObjectData> layer = RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex);
			layer.Name = item.DataAfter.Name;
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item.CategoryIndex, item.LayerIndex, layer);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex);
			break;
		}
		case EditHistoryLayerAction.Remove:
			EditRemoveLayer(Category.ToName(item.CategoryIndex), item.LayerIndex, EditRemoveLayerSource.History);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerRemove, item.CategoryIndex, item.LayerIndex);
			break;
		case EditHistoryLayerAction.Copy:
			EditCopyLayer(Category.ToName(item.CategoryIndex), item.LayerIndex, EditCopyLayerSource.History);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerAdd, item.CategoryIndex, item.LayerIndex + 1, RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex + 1));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex + 1);
			break;
		case EditHistoryLayerAction.Add:
			EditAddLayer(Category.ToName(item.CategoryIndex), item.LayerIndex, item.DataAfter.Name, addHistory: false);
			((SFDLayerTag)RenderCategories[item.DataAfter.CategoryIndex].GetLayer(item.DataAfter.LayerIndex).Tag).Properties.SetValues(item.DataAfter.Properties, networkSilent: true);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerAdd, item.CategoryIndex, item.LayerIndex, RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex);
			break;
		case EditHistoryLayerAction.MoveUp:
		case EditHistoryLayerAction.MoveDown:
			EditChangeLayers(Category.ToName(item.CategoryIndex), item.LayerIndex, item.LayerIndex + 1, addHistory: false);
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item.CategoryIndex, item.LayerIndex, RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item.CategoryIndex, item.LayerIndex + 1, RenderCategories[item.CategoryIndex].GetLayer(item.LayerIndex + 1));
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, item.CategoryIndex, item.LayerIndex + ((item.Action != EditHistoryLayerAction.MoveDown) ? 1 : 0));
			break;
		}
	}

	public bool AllLayersLocked(int categoryIndex)
	{
		bool result = RenderCategories[categoryIndex].TotalLayers > 0;
		for (int i = 0; i < RenderCategories[categoryIndex].TotalLayers; i++)
		{
			if (!RenderCategories[categoryIndex].GetLayer(i).IsLocked)
			{
				result = false;
				break;
			}
		}
		return result;
	}

	public void EditChangeLayerName(string category, int layerIndex, string newName, bool addHistory)
	{
		int num = Category.ToIndex(category);
		if (num == -1)
		{
			throw new Exception("Error Editor: Could not set layer properties (name), categoryIndex is -1 for " + category);
		}
		Layer<ObjectData> layer = RenderCategories[num].GetLayer(layerIndex);
		EditHistoryItemLayerData dataBefore = new EditHistoryItemLayerData(num, layerIndex, layer.Name, null);
		EditHistoryItemLayerData dataAfter = new EditHistoryItemLayerData(num, layerIndex, newName, null);
		layer.Name = newName;
		if (addHistory)
		{
			EditHistoryItemLayer editHistoryItem = new EditHistoryItemLayer(num, layerIndex, EditHistoryLayerAction.Rename, dataBefore, dataAfter);
			EditHistoryAddEntry(editHistoryItem);
		}
	}

	public bool EditRemoveLayerPossible(string category, int layerIndex)
	{
		int num = Category.ToIndex(category);
		if (num == -1)
		{
			throw new Exception("Error Editor: Could not copy layer, categoryIndex is -1 for " + category + ", category must exist first");
		}
		Layer<ObjectData> layer = RenderCategories[num].GetLayer(layerIndex);
		if (EditGetGroupIDsFromObjects(layer.Items).Count > 0 && MessageBox.Show(LanguageHelper.GetText("mapEditor.removeLayer.warnAboutGroups", $"{category} - {layer.Name}"), LanguageHelper.GetText("mapEditor.groupsExist"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.Cancel)
		{
			return false;
		}
		return true;
	}

	public void EditRemoveLayer(string category, int layerIndex, EditRemoveLayerSource layerSource)
	{
		int num = Category.ToIndex(category);
		if (num == -1)
		{
			throw new Exception("Error Editor: Could not remove layer, categoryIndex is -1 for " + category);
		}
		EditSetPhysicsOff();
		bool flag = layerSource == EditRemoveLayerSource.User;
		Layer<ObjectData> layer = RenderCategories[num].GetLayer(layerIndex);
		if (layer != null)
		{
			string name = layer.Name;
			List<ObjectData> list = new List<ObjectData>(layer.Items.Count);
			foreach (ObjectData item in layer.Items)
			{
				list.Add(item);
			}
			foreach (ushort item2 in EditGetGroupIDsFromObjects(layer.Items))
			{
				foreach (ObjectData item3 in GetObjectDataByGroupID(item2))
				{
					if (!list.Contains(item3))
					{
						list.Add(item3);
					}
				}
			}
			if (flag)
			{
				EditHistoryGroupBegin();
			}
			foreach (ObjectData item4 in list)
			{
				if (flag)
				{
					EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(item4);
					EditHistoryAddEntry(new EditHistoryItemObject(item4, EditHistoryObjectAction.Deletion, dataBefore, null));
				}
				EditSelectedObjects.Remove(item4);
				item4.Remove();
			}
			object[] properties = ((SFDLayerTag)layer.Tag).Properties.ToObjectArray();
			((SFDLayerTag)layer.Tag).Dispose();
			layer.Tag = null;
			RenderCategories[num].RemoveLayer(layerIndex);
			EditAdjustObjectsFromLayer(num, layerIndex);
			if (flag)
			{
				EditHistoryItemLayerData dataBefore2 = new EditHistoryItemLayerData(num, layerIndex, name, properties);
				EditHistoryAddEntry(new EditHistoryItemLayer(num, layerIndex, EditHistoryLayerAction.Remove, dataBefore2, null));
				EditHistoryGroupEnd();
			}
			list.Clear();
			list = null;
		}
		layer = null;
	}

	public bool EditCopyLayerPossible(string category, int layerIndex)
	{
		int num = Category.ToIndex(category);
		if (num == -1)
		{
			throw new Exception("Error Editor: Could not copy layer, categoryIndex is -1 for " + category + ", category must exist first");
		}
		Layer<ObjectData> layer = RenderCategories[num].GetLayer(layerIndex);
		if (EditGetGroupIDsFromObjects(layer.Items).Count > 0 && MessageBox.Show(LanguageHelper.GetText("mapEditor.copyLayer.warnAboutGroups", $"{category} - {layer.Name}"), LanguageHelper.GetText("mapEditor.groupsExist"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.Cancel)
		{
			return false;
		}
		return true;
	}

	public void EditCopyLayer(string category, int layerIndexToCopy, EditCopyLayerSource copySource)
	{
		int num = Category.ToIndex(category);
		if (num == -1)
		{
			throw new Exception("Error Editor: Could not copy layer, categoryIndex is -1 for " + category + ", category must exist first");
		}
		EditSetPhysicsOff();
		bool flag = copySource == EditCopyLayerSource.User;
		RenderCategories[num].AddLayer(layerIndexToCopy + 1);
		Layer<ObjectData> layer = RenderCategories[num].GetLayer(layerIndexToCopy);
		Layer<ObjectData> layer2 = RenderCategories[num].GetLayer(layerIndexToCopy + 1);
		if (flag)
		{
			EditHistoryGroupBegin();
			EditHistoryAddEntry(new EditHistoryItemLayer(num, layerIndexToCopy, EditHistoryLayerAction.Copy, null, null));
		}
		EditSelectedObjects.Clear();
		EditAdjustObjectsFromLayer(num, layerIndexToCopy + 1);
		layer2.Name = layer.Name + " (copy)";
		SetLayerPropertiesData(num, layerIndexToCopy + 1);
		((SFDLayerTag)layer2.Tag).Properties.SetValues(((SFDLayerTag)layer.Tag).Properties.ToObjectArray(), networkSilent: true);
		List<ObjectData> list = new List<ObjectData>(layer.Items.Count);
		if (flag)
		{
			foreach (ObjectData item in layer.Items)
			{
				if (item.GroupID == 0)
				{
					list.Add(item);
				}
			}
			Dictionary<int, int> dictionary = new Dictionary<int, int>();
			List<ObjectData> list2 = new List<ObjectData>();
			foreach (ObjectData item2 in list)
			{
				ObjectData objectData = EditCreatePreviewObject(item2, item2.GetWorldPosition());
				objectData.SetRenderLayer(layerIndexToCopy + 1);
				list2.Add(objectData);
				dictionary.Add(item2.ObjectID, objectData.ObjectID);
			}
			EditProcessNewIDs(list2, dictionary);
			dictionary.Clear();
			dictionary = null;
			foreach (ObjectData item3 in list2)
			{
				EditHistoryAddEntry(new EditHistoryItemObject(item3, EditHistoryObjectAction.Creation, null, new EditHistoryItemObjectData(item3)));
			}
			list2.Clear();
			list2 = null;
		}
		EditPreviewObjects.Clear();
		if (flag)
		{
			EditHistoryGroupEnd();
		}
		list.Clear();
		list = null;
	}

	public void EditAddLayer(string category, int layerIndex, string newName, bool addHistory)
	{
		int num = Category.ToIndex(category);
		if (num == -1)
		{
			throw new Exception("Error Editor: Could not add layer, categoryIndex is -1 for " + category + ", category must exist first");
		}
		EditAddLayer(num, layerIndex, newName, addHistory);
	}

	public void EditAddLayer(int categoryIndex, int layerIndex, string newName, bool addHistory)
	{
		if (categoryIndex == -1)
		{
			throw new Exception("Error Editor: Could not add layer, categoryIndex is -1, category must exist first");
		}
		EditSetPhysicsOff();
		RenderCategories[categoryIndex].AddLayer(layerIndex);
		Layer<ObjectData> layer = RenderCategories[categoryIndex].GetLayer(layerIndex);
		layer.Name = newName;
		SetLayerPropertiesData(categoryIndex, layerIndex);
		object[] properties = ((SFDLayerTag)layer.Tag).Properties.ToObjectArray();
		if (addHistory)
		{
			EditHistoryAddEntry(new EditHistoryItemLayer(categoryIndex, layerIndex, EditHistoryLayerAction.Add, null, new EditHistoryItemLayerData(categoryIndex, layerIndex, newName, properties)));
		}
		EditAdjustObjectsFromLayer(categoryIndex, layerIndex);
	}

	public void EditChangeLayers(string category, int layerIndexA, int layerIndexB, bool addHistory)
	{
		if (layerIndexA == layerIndexB)
		{
			return;
		}
		bool flag = layerIndexA < layerIndexB;
		if (layerIndexA > layerIndexB)
		{
			int num = layerIndexA;
			layerIndexA = layerIndexB;
			layerIndexB = num;
		}
		int num2 = Category.ToIndex(category);
		if (num2 == -1)
		{
			throw new Exception("Error Editor: Could not change layers, categoryIndex is -1 for " + category + ", category must exist first");
		}
		if (addHistory)
		{
			EditHistoryAddEntry(new EditHistoryItemLayer(num2, layerIndexA, flag ? EditHistoryLayerAction.MoveUp : EditHistoryLayerAction.MoveDown, null, null));
		}
		Layer<ObjectData> layer = RenderCategories[num2].GetLayer(layerIndexA);
		Layer<ObjectData> layer2 = RenderCategories[num2].GetLayer(layerIndexB);
		RenderCategories[num2].RemoveLayer(layer);
		RenderCategories[num2].RemoveLayer(layer2);
		RenderCategories[num2].InsertLayer(layerIndexA, layer2);
		RenderCategories[num2].InsertLayer(layerIndexB, layer);
		List<ObjectData> list = new List<ObjectData>(layer.Items.Count);
		List<ObjectData> list2 = new List<ObjectData>(layer2.Items.Count);
		foreach (ObjectData item in layer.Items)
		{
			list.Add(item);
		}
		foreach (ObjectData item2 in layer2.Items)
		{
			list2.Add(item2);
		}
		foreach (ObjectData item3 in list)
		{
			item3.SetRenderLayer(layerIndexB);
		}
		foreach (ObjectData item4 in list2)
		{
			item4.SetRenderLayer(layerIndexA);
		}
		list.Clear();
		list = null;
		list2.Clear();
		list2 = null;
		layer = null;
		layer2 = null;
	}

	public void EditSelectLayer(string category, int layerIndex)
	{
		if (m_editSelectedLayers == null)
		{
			m_editSelectedLayers = new Dictionary<string, int>(40);
		}
		if (!m_editSelectedLayers.ContainsKey(category))
		{
			m_editSelectedLayers.Add(category, 0);
		}
		m_editSelectedLayers[category] = layerIndex;
	}

	public int EditGetLastSelectedLayer(int categoryIndex)
	{
		if (categoryIndex == -1)
		{
			return 0;
		}
		if (m_editSelectedLayers == null)
		{
			m_editSelectedLayers = new Dictionary<string, int>(40);
		}
		string key = Category.ToName(categoryIndex);
		if (!m_editSelectedLayers.ContainsKey(key))
		{
			m_editSelectedLayers.Add(key, 0);
		}
		int num = m_editSelectedLayers[key];
		if (RenderCategories[categoryIndex].TotalLayers < num)
		{
			return 0;
		}
		return num;
	}

	public int EditGetLastSelectedLayer(string category)
	{
		if (m_editSelectedLayers == null)
		{
			m_editSelectedLayers = new Dictionary<string, int>(40);
		}
		if (!m_editSelectedLayers.ContainsKey(category))
		{
			m_editSelectedLayers.Add(category, 0);
		}
		int num = m_editSelectedLayers[category];
		int num2 = Category.ToIndex(category);
		if (num2 == -1)
		{
			return 0;
		}
		if (RenderCategories[num2].TotalLayers < num)
		{
			return 0;
		}
		return num;
	}

	public void EditAdjustObjectsFromLayer(int categoryIndex, int layerIndex)
	{
		Layer<ObjectData> layer = null;
		List<ObjectData> list = new List<ObjectData>();
		for (int i = layerIndex; i < RenderCategories[categoryIndex].TotalLayers; i++)
		{
			layer = RenderCategories[categoryIndex].GetLayer(i);
			if (layer == null)
			{
				continue;
			}
			list.Clear();
			foreach (ObjectData item in layer.Items)
			{
				list.Add(item);
			}
			foreach (ObjectData item2 in list)
			{
				item2.SetRenderLayer(i);
			}
		}
		layer = null;
	}

	public void EditSelectObjectsInLayer(string category, int layerIndex)
	{
		EditSetPhysicsOff();
		EditSelectedObjects.Clear();
		int num = Category.ToIndex(category);
		if (num != -1)
		{
			Layer<ObjectData> layer = RenderCategories[num].GetLayer(layerIndex);
			if (layer.IsLocked || !layer.IsVisible)
			{
				return;
			}
			foreach (ObjectData item in layer.Items)
			{
				if (EditGroupID <= 0 || item.GroupID == EditGroupID)
				{
					EditSelectedObjects.Add(item);
				}
			}
			EditSelectedObjectsHandlePostAdd(EditSelectedObjects, EditSelectedObjectsOptions.GroupCheck);
		}
		EditSetMapEditorPropertiesWindow();
	}

	public void EditGetLayerTag(ObjectData layerTagObject, ref int categoryIndex, ref int layerIndex)
	{
		categoryIndex = -1;
		layerIndex = -1;
		for (int i = 0; i < 30; i++)
		{
			for (int j = 0; j < RenderCategories[i].TotalLayers; j++)
			{
				if (((SFDLayerTag)RenderCategories[i].GetLayer(j).Tag).Object == layerTagObject)
				{
					categoryIndex = i;
					layerIndex = j;
					return;
				}
			}
		}
	}

	public void ReadFromStream(Stream stream, int startPosition)
	{
		Constants.SetThreadCultureInfo();
		bool inLoading = InLoading;
		InLoading = true;
		if (startPosition == -1)
		{
			startPosition = 0;
		}
		stream.Position = startPosition;
		bool flag = false;
		MapInfo mapInfo = MapInfo.ReadMapHeader(stream, "");
		if (mapInfo.IsOfficial)
		{
			EditReadOnly = true;
		}
		flag = mapInfo.IsTemplate;
		mapInfo.Dispose();
		using (SFDBinaryReader sFDBinaryReader = new SFDBinaryReader(stream))
		{
			sFDBinaryReader.AutoCloseStream = false;
			string text = sFDBinaryReader.ReadString();
			while (text != "EOF")
			{
				switch (text)
				{
				case "c_so":
				{
					int num2 = sFDBinaryReader.ReadInt32();
					for (int j = 0; j < num2; j++)
					{
						ObjectData.Load(this, sFDBinaryReader);
					}
					break;
				}
				case "c_tl":
				{
					CustomIDTableLookup = new Dictionary<string, int>();
					int num7 = sFDBinaryReader.ReadInt32();
					for (int n = 0; n < num7; n++)
					{
						string key = sFDBinaryReader.ReadString();
						int value2 = sFDBinaryReader.ReadInt32();
						CustomIDTableLookup.Add(key, value2);
					}
					break;
				}
				case "c_wp":
				{
					List<ObjectProperties.ObjectPropertyValueInstance> values2 = ObjectProperties.Load(sFDBinaryReader, networkSilent: true);
					ObjectWorldData.Properties.Load(values2);
					ObjectWorldData.MapIsTemplate = flag;
					break;
				}
				case "c_lr":
				{
					int num8 = sFDBinaryReader.ReadInt32();
					for (int num9 = 0; num9 < num8; num9++)
					{
						string value3 = sFDBinaryReader.ReadString();
						int num10 = -1;
						try
						{
							Category.TYPE result2 = Category.TYPE.BG;
							num10 = (int)(Enum.TryParse<Category.TYPE>(value3, out result2) ? result2 : ((Category.TYPE)(-1)));
						}
						catch
						{
							num10 = -1;
						}
						int num11 = sFDBinaryReader.ReadInt32();
						for (int num12 = 0; num12 < num11; num12++)
						{
							bool isLocked = sFDBinaryReader.ReadBoolean();
							bool isVisible = sFDBinaryReader.ReadBoolean();
							string name = sFDBinaryReader.ReadString();
							if (num10 >= 0 && num10 < RenderCategories.Categories.Length && num12 >= 0)
							{
								RenderCategories.Categories[num10].AddLayer(num12);
								Layer<ObjectData> layer = RenderCategories.Categories[num10].GetLayer(num12);
								layer.Name = name;
								layer.IsLocked = isLocked;
								layer.IsVisible = isVisible;
								SetLayerPropertiesData(num10, num12);
							}
						}
					}
					break;
				}
				case "c_do":
				{
					int num6 = sFDBinaryReader.ReadInt32();
					for (int m = 0; m < num6; m++)
					{
						ObjectData.Load(this, sFDBinaryReader);
					}
					break;
				}
				case "c_lrp":
				{
					int num3 = sFDBinaryReader.ReadInt32();
					for (int k = 0; k < num3; k++)
					{
						string value = sFDBinaryReader.ReadString();
						int num4 = -1;
						try
						{
							Category.TYPE result = Category.TYPE.BG;
							num4 = (int)(Enum.TryParse<Category.TYPE>(value, out result) ? result : ((Category.TYPE)(-1)));
						}
						catch
						{
							num4 = -1;
						}
						int num5 = sFDBinaryReader.ReadInt32();
						for (int l = 0; l < num5; l++)
						{
							List<ObjectProperties.ObjectPropertyValueInstance> values = ObjectProperties.Load(sFDBinaryReader, networkSilent: true);
							if (num4 >= 0 && num4 < RenderCategories.Categories.Length && l >= 0)
							{
								((SFDLayerTag)RenderCategories.Categories[num4].GetLayer(l).Tag).Properties.Load(values);
							}
						}
					}
					break;
				}
				case "c_fbgp":
					ObjectProperties.Load(sFDBinaryReader, networkSilent: true);
					break;
				case "c_sobjs":
				{
					int num = sFDBinaryReader.ReadInt32();
					for (int i = 0; i < num; i++)
					{
						ObjectData.Load(this, sFDBinaryReader);
					}
					break;
				}
				case "c_scrpt":
				{
					byte[] bytes = Convert.FromBase64String(sFDBinaryReader.ReadString());
					SetInnerScript(Encoding.UTF8.GetString(bytes));
					break;
				}
				default:
					ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Error: Loading information '" + text + "' failed.");
					text = "EOF";
					EditMapFileIsCorrupt = true;
					break;
				}
				if (text != "EOF")
				{
					text = sFDBinaryReader.ReadString();
				}
			}
		}
		HandleObjectsOnLoad();
		FinalizeProperties();
		HandleObjectCleanCycle();
		InLoading = inLoading;
	}

	public void WriteToStream(Stream stream, MapPartHeaderInfo mapPartHeaderInfo = null)
	{
		if (!EditMode)
		{
			throw new NotSupportedException("Can only save maps in edit mode in the editor.");
		}
		FinalizeProperties();
		List<ObjectData> list = new List<ObjectData>();
		List<ObjectData> list2 = new List<ObjectData>();
		foreach (ObjectData renderCategory in RenderCategories)
		{
			list2.Add(renderCategory);
			list.Add(renderCategory);
			renderCategory.EditBeforeSave();
		}
		int num = 0;
		Dictionary<int, int> dictionary = new Dictionary<int, int>();
		Dictionary<ObjectPropertyInstance, object> dictionary2 = new Dictionary<ObjectPropertyInstance, object>();
		Dictionary<string, int> dictionary3 = new Dictionary<string, int>();
		foreach (ObjectData item in list)
		{
			string text = (string)item.Properties.Get(ObjectPropertyID.Object_CustomID).Value;
			if (text != "" && !dictionary3.ContainsKey(text))
			{
				dictionary3.Add(text, dictionary3.Count + 1);
			}
			num++;
			dictionary.Add(item.ObjectID, num);
		}
		foreach (ObjectData item2 in list)
		{
			item2.ObjectIDCompression(dictionary, dictionary2);
		}
		dictionary.Clear();
		dictionary = null;
		using (SFDBinaryWriter sFDBinaryWriter = new SFDBinaryWriter(stream))
		{
			sFDBinaryWriter.AutoCloseStream = false;
			string mapName = ObjectWorldData.MapName;
			string mapAuthor = ObjectWorldData.MapAuthor;
			int mapType = (int)ObjectWorldData.MapType;
			int mapTotalPlayers = ObjectWorldData.MapTotalPlayers;
			bool mapIsTemplate = ObjectWorldData.MapIsTemplate;
			bool mapEditLock = ObjectWorldData.MapEditLock;
			string mapPublishExternalID = ObjectWorldData.MapPublishExternalID;
			string mapDescription = ObjectWorldData.MapDescription;
			string mapTags = ObjectWorldData.MapTags;
			string mapScriptTypes = ObjectWorldData.MapScriptTypes;
			sFDBinaryWriter.Write("h_gv");
			sFDBinaryWriter.Write(Guid.NewGuid());
			sFDBinaryWriter.Write("v.1.5.0e");
			if (mapPartHeaderInfo != null)
			{
				sFDBinaryWriter.Write("h_or");
				sFDBinaryWriter.Write(mapPartHeaderInfo.OriginalGuid);
				sFDBinaryWriter.Write(mapPartHeaderInfo.OwnerHash);
			}
			sFDBinaryWriter.Write("h_tmp");
			sFDBinaryWriter.Write(mapIsTemplate);
			sFDBinaryWriter.Write("h_el");
			sFDBinaryWriter.Write(mapEditLock);
			sFDBinaryWriter.Write("h_wn");
			sFDBinaryWriter.Write(mapName);
			sFDBinaryWriter.Write("h_wa");
			sFDBinaryWriter.Write(mapAuthor);
			sFDBinaryWriter.Write("h_mtp");
			sFDBinaryWriter.Write(mapType);
			sFDBinaryWriter.Write(mapTotalPlayers);
			sFDBinaryWriter.Write("h_tg");
			sFDBinaryWriter.Write(mapTags);
			sFDBinaryWriter.Write("h_wd");
			sFDBinaryWriter.Write(mapDescription);
			sFDBinaryWriter.Write("h_wdt");
			DateTime now = DateTime.Now;
			sFDBinaryWriter.Write(now.Year);
			sFDBinaryWriter.Write(now.Month);
			sFDBinaryWriter.Write(now.Day);
			sFDBinaryWriter.Write(now.Hour);
			sFDBinaryWriter.Write(now.Minute);
			sFDBinaryWriter.Write("h_pei");
			sFDBinaryWriter.Write(mapPublishExternalID);
			sFDBinaryWriter.Write("h_mt");
			sFDBinaryWriter.Write("SFDMAPEDIT".ToCharArray(), 0, 10);
			if (mapPartHeaderInfo != null)
			{
				if (IsExtensionScript)
				{
					sFDBinaryWriter.Write("h_ext");
					sFDBinaryWriter.Write(mapScriptTypes);
					sFDBinaryWriter.Write("h_exscript\n");
					sFDBinaryWriter.WriteStringNullDelimiter(GetInnerScript());
				}
				sFDBinaryWriter.Write("h_pt");
				sFDBinaryWriter.Write(mapPartHeaderInfo.Parts.Length);
				for (int i = 0; i < mapPartHeaderInfo.Parts.Length; i++)
				{
					sFDBinaryWriter.Write(mapPartHeaderInfo.Parts[i].Name);
					sFDBinaryWriter.Write(mapPartHeaderInfo.Parts[i].Selectable);
					mapPartHeaderInfo.Parts[i].StartPosition = (int)stream.Position;
					sFDBinaryWriter.Write(int.MinValue);
				}
				if (mapPartHeaderInfo.ThumbnailData != null && MapThumbnailHandler.CheckValidSize(mapPartHeaderInfo.ThumbnailData))
				{
					sFDBinaryWriter.Write("h_img");
					sFDBinaryWriter.Write(mapPartHeaderInfo.ThumbnailData.Length);
					sFDBinaryWriter.Write(mapPartHeaderInfo.ThumbnailData, 0, mapPartHeaderInfo.ThumbnailData.Length);
				}
			}
			sFDBinaryWriter.Write("c_wp");
			ObjectProperties.Save(PropertiesWorld, sFDBinaryWriter);
			if (!IsExtensionScript)
			{
				sFDBinaryWriter.Write("c_scrpt");
				string value = Convert.ToBase64String(Encoding.UTF8.GetBytes(GetInnerScript()));
				sFDBinaryWriter.Write(value);
			}
			sFDBinaryWriter.Write("c_lr");
			sFDBinaryWriter.Write(RenderCategories.Categories.Length);
			for (int j = 0; j < RenderCategories.Categories.Length; j++)
			{
				Category.TYPE tYPE = (Category.TYPE)j;
				string value2 = tYPE.ToString();
				sFDBinaryWriter.Write(value2);
				sFDBinaryWriter.Write(RenderCategories.Categories[j].TotalLayers);
				for (int k = 0; k < RenderCategories.Categories[j].TotalLayers; k++)
				{
					Layer<ObjectData> layer = RenderCategories.Categories[j].GetLayer(k);
					sFDBinaryWriter.Write(layer.IsLocked);
					sFDBinaryWriter.Write(layer.IsVisible);
					sFDBinaryWriter.Write(layer.Name);
				}
			}
			sFDBinaryWriter.Write("c_lrp");
			sFDBinaryWriter.Write(RenderCategories.Categories.Length);
			for (int l = 0; l < RenderCategories.Categories.Length; l++)
			{
				Category.TYPE tYPE2 = (Category.TYPE)l;
				string value3 = tYPE2.ToString();
				sFDBinaryWriter.Write(value3);
				sFDBinaryWriter.Write(RenderCategories.Categories[l].TotalLayers);
				for (int m = 0; m < RenderCategories.Categories[l].TotalLayers; m++)
				{
					ObjectProperties.Save(((SFDLayerTag)RenderCategories.Categories[l].GetLayer(m).Tag).Properties, sFDBinaryWriter);
				}
			}
			sFDBinaryWriter.Write("c_tl");
			sFDBinaryWriter.Write(dictionary3.Keys.Count);
			foreach (KeyValuePair<string, int> item3 in dictionary3)
			{
				sFDBinaryWriter.Write(item3.Key);
				sFDBinaryWriter.Write(item3.Value);
			}
			sFDBinaryWriter.Write("c_sobjs");
			sFDBinaryWriter.Write(list2.Count);
			for (int n = 0; n < list2.Count; n++)
			{
				ObjectData.Save(list2[n], sFDBinaryWriter);
			}
			sFDBinaryWriter.Write("EOF");
			foreach (ObjectData item4 in list)
			{
				item4.ObjectIDUnCompression(dictionary2);
			}
			dictionary2.Clear();
			dictionary2 = null;
			sFDBinaryWriter.Flush();
		}
		foreach (ObjectData item5 in list)
		{
			item5.EditAfterSave();
		}
		list.Clear();
		list = null;
		list2.Clear();
		list2 = null;
	}

	public ObjectData EditCreatePreviewObject(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPos, float angle, short faceDirection)
	{
		EditSetPhysicsOff();
		ObjectData objectData = BodyData.Read(CreateTile(mapObjectID, worldPos, angle, faceDirection, Microsoft.Xna.Framework.Vector2.Zero, 0f)).Object;
		objectData.Properties.SetValues(objectData.Properties.ToObjectArray());
		objectData.ApplyColors(objectData.Colors);
		objectData.SetRenderLayer(EditGetLastSelectedLayer(objectData.Tile.DrawCategory));
		objectData.SetGroupID(EditGroupID);
		EditPreviewObjects.Add(objectData);
		return objectData;
	}

	public ObjectData EditCreatePreviewObjectSingle(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPos, float angle, short faceDirection)
	{
		EditSetPhysicsOff();
		EditRemovePreviewObjects();
		EditPreviewObjectsHistoryEntries = new List<EditHistoryItemObject>();
		ObjectData objectData = BodyData.Read(CreateTile(mapObjectID, worldPos, angle, faceDirection, Microsoft.Xna.Framework.Vector2.Zero, 0f)).Object;
		objectData.Properties.SetValues(objectData.Properties.ToObjectArray());
		objectData.ApplyColors(objectData.Colors);
		objectData.SetRenderLayer(EditGetLastSelectedLayer(objectData.Tile.DrawCategory));
		objectData.SetGroupID(EditGroupID);
		EditPreviewObjects.Add(objectData);
		EditPreviewObjectsHistoryEntries.Add(new EditHistoryItemObject(objectData, EditHistoryObjectAction.Creation, null, new EditHistoryItemObjectData(objectData)));
		return objectData;
	}

	public ObjectData EditCreatePreviewObject(ObjectData objectData, Microsoft.Xna.Framework.Vector2 worldPos)
	{
		EditSetPhysicsOff();
		ObjectData objectData2 = BodyData.Read(CreateTile(objectData.MapObjectID, worldPos, objectData.GetAngle(), objectData.FaceDirection, Microsoft.Xna.Framework.Vector2.Zero, 0f)).Object;
		objectData2.Properties.SetValues(objectData.Properties.ToObjectArray());
		objectData2.ApplyColors(objectData.Colors);
		objectData2.SetRenderLayer(EditGetLastSelectedLayer(objectData2.Tile.DrawCategory));
		objectData2.SetGroupID(EditGroupID);
		EditPreviewObjects.Add(objectData2);
		return objectData2;
	}

	public void EditUpdatePreviewObjects(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (EditPreviewObjects.Count <= 0)
		{
			return;
		}
		m_editPreviewOffsets.Clear();
		foreach (ObjectData editPreviewObject in EditPreviewObjects)
		{
			m_editPreviewOffsets.Add(editPreviewObject.Body.GetPosition() - EditPreviewObjects[0].Body.GetPosition());
		}
		Microsoft.Xna.Framework.Vector2 worldCoordinate = EditSnapPositionToGrid(worldPosition + EditMouseOffset, EditPreviewObjects[0], snapX: true, snapY: true);
		for (int i = 0; i < EditPreviewObjects.Count; i++)
		{
			EditPreviewObjects[i].Body.SetTransform(Converter.ConvertWorldToBox2D(worldCoordinate) + m_editPreviewOffsets[i], EditPreviewObjects[i].Body.GetAngle());
		}
	}

	public void EditRemovePreviewObjects()
	{
		foreach (ObjectData editPreviewObject in EditPreviewObjects)
		{
			editPreviewObject.Remove();
		}
		EditPreviewObjects.Clear();
		if (EditPreviewObjectsHistoryEntries != null)
		{
			EditPreviewObjectsHistoryEntries.Clear();
			EditPreviewObjectsHistoryEntries = null;
		}
	}

	public bool EditCheckLayersEnabledForEdit(IEnumerable<ObjectData> objectsToCheck, EditCheckLayersEnabledForEditMode textMode = EditCheckLayersEnabledForEditMode.Edit)
	{
		List<ItemContainer<int, int, Layer<ObjectData>>> list = new List<ItemContainer<int, int, Layer<ObjectData>>>();
		HashSet<string> hashSet = new HashSet<string>();
		foreach (ObjectData item2 in objectsToCheck)
		{
			Layers<ObjectData> layers = RenderCategories[item2.Tile.DrawCategory];
			if (layers == null || layers.TotalLayers <= item2.LocalRenderLayer)
			{
				continue;
			}
			Layer<ObjectData> layer = layers.GetLayer(item2.LocalRenderLayer);
			if (layer != null && (layer.IsLocked || !layer.IsVisible))
			{
				int drawCategory = item2.Tile.DrawCategory;
				int localRenderLayer = item2.LocalRenderLayer;
				string item = $"{drawCategory}_{localRenderLayer}";
				if (hashSet.Add(item))
				{
					list.Add(new ItemContainer<int, int, Layer<ObjectData>>(drawCategory, localRenderLayer, layer));
				}
			}
		}
		hashSet.Clear();
		if (list.Count > 0)
		{
			string text = "";
			string text2 = "";
			if (textMode == EditCheckLayersEnabledForEditMode.GroupSelection)
			{
				text = LanguageHelper.GetText("mapEditor.layersLockedHidden");
				text2 = LanguageHelper.GetText("mapEditor.groupSelection.layersLockedHidden.askToUnlockShow");
			}
			else if (list.Count > 1)
			{
				text = LanguageHelper.GetText("mapEditor.layersLockedHidden");
				text2 = LanguageHelper.GetText("mapEditor.layersLockedHidden.askToUnlockShow");
			}
			else
			{
				text = LanguageHelper.GetText("mapEditor.layerLockedHidden");
				text2 = LanguageHelper.GetText("mapEditor.layerLockedHidden.askToUnlockShow", list[0].Item3.Name);
			}
			if (MessageBox.Show(text2, text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
			{
				foreach (ItemContainer<int, int, Layer<ObjectData>> item3 in list)
				{
					item3.Item3.IsLocked = false;
					item3.Item3.IsVisible = true;
					m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, item3.Item1, item3.Item2, item3.Item3);
				}
				list.Clear();
				return true;
			}
			list.Clear();
			return false;
		}
		return true;
	}

	public void EditPlacePreviewObjects()
	{
		EditSetPhysicsOff();
		if (EditPreviewObjects.Count <= 0)
		{
			return;
		}
		if (!EditCheckLayersEnabledForEdit(EditPreviewObjects))
		{
			EditRemovePreviewObjects();
			return;
		}
		ObjectPathNodeConnection objectPathNodeConnection = null;
		ObjectPathNode originalSelectedPathNode = null;
		if (EditPreviewObjects.Count == 1 && EditSelectedObjects.Count == 1 && EditPreviewObjects[0] is ObjectPathNode && EditSelectedObjects[0] is ObjectPathNode)
		{
			originalSelectedPathNode = (ObjectPathNode)EditSelectedObjects[0];
			ObjectPathNode objectPathNode = (ObjectPathNode)GetObjectAtMousePosition(autoUnlockLockedLayers: true, autoShowHiddenLayers: true, prioritizeDynamicObjects: false, (ObjectData filterOd) => filterOd is ObjectPathNode && filterOd != originalSelectedPathNode && !EditPreviewObjects.Contains(filterOd));
			if (objectPathNode != null)
			{
				EditRemovePreviewObjects();
				Microsoft.Xna.Framework.Vector2 worldPos = objectPathNode.GetWorldCenterPosition() + (originalSelectedPathNode.GetWorldCenterPosition() - objectPathNode.GetWorldCenterPosition()) * 0.5f;
				objectPathNodeConnection = (ObjectPathNodeConnection)EditCreatePreviewObjectSingle("PathNodeConnection", worldPos, 0f, 1);
				objectPathNodeConnection.SetPathNodeA(originalSelectedPathNode);
				objectPathNodeConnection.SetPathNodeB(objectPathNode);
			}
			else
			{
				Microsoft.Xna.Framework.Vector2 worldPos2 = EditPreviewObjects[0].GetWorldCenterPosition() + (originalSelectedPathNode.GetWorldCenterPosition() - EditPreviewObjects[0].GetWorldCenterPosition()) * 0.5f;
				objectPathNodeConnection = (ObjectPathNodeConnection)EditCreatePreviewObject("PathNodeConnection", worldPos2, 0f, 1);
				objectPathNodeConnection.SetPathNodeA(originalSelectedPathNode);
				objectPathNodeConnection.SetPathNodeB((ObjectPathNode)EditPreviewObjects[0]);
				EditPreviewObjectsHistoryEntries.Add(new EditHistoryItemObject(objectPathNodeConnection, EditHistoryObjectAction.Creation, null, new EditHistoryItemObjectData(objectPathNodeConnection)));
				originalSelectedPathNode = null;
			}
		}
		EditSelectedObjects.Clear();
		EditSelectedObjects.AddRange(EditPreviewObjects);
		EditSelectedObjects.Remove(objectPathNodeConnection);
		if (originalSelectedPathNode != null)
		{
			EditSelectedObjects.Add(originalSelectedPathNode);
		}
		EditSelectedObjectsHandlePostAdd(EditPreviewObjects, EditSelectedObjectsOptions.None);
		EditAfterSelection();
		foreach (ObjectData editPreviewObject in EditPreviewObjects)
		{
			editPreviewObject.FinalizeProperties();
		}
		foreach (ObjectData editPreviewObject2 in EditPreviewObjects)
		{
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerShow, editPreviewObject2.Tile.DrawCategory, editPreviewObject2.LocalRenderLayer, RenderCategories[editPreviewObject2.Tile.DrawCategory].GetLayer(editPreviewObject2.LocalRenderLayer));
		}
		EditHistoryGroupBegin();
		foreach (EditHistoryItemObject editPreviewObjectsHistoryEntry in EditPreviewObjectsHistoryEntries)
		{
			foreach (ObjectData editPreviewObject3 in EditPreviewObjects)
			{
				if (editPreviewObjectsHistoryEntry.ObjectId == editPreviewObject3.ObjectID)
				{
					editPreviewObjectsHistoryEntry.DataAfter.SetValuesFromObject(editPreviewObject3);
					EditPreviewObjects.Remove(editPreviewObject3);
					break;
				}
			}
			EditHistoryAddEntry(editPreviewObjectsHistoryEntry);
		}
		EditHistoryGroupEnd();
		EditPreviewObjects.Clear();
		EditPreviewObjectsHistoryEntries.Clear();
		EditPreviewObjectsHistoryEntries = null;
	}

	public EditSearchResult EditSearch(string searchValue)
	{
		EditSearchResult editSearchResult = new EditSearchResult(this, searchValue);
		int result = 0;
		if (!int.TryParse(searchValue, out result))
		{
			result = 0;
		}
		foreach (ObjectData item in AllObjectData())
		{
			if (result != 0 && item.ObjectID == result)
			{
				editSearchResult.Result.Add(new EditSearchResultItem(item, LanguageHelper.GetText("properties.baseid"), item.ObjectID.ToString(), EditSearchResultMatchType.Full));
			}
			if (string.Compare(item.MapObjectID, searchValue, ignoreCase: true) == 0)
			{
				editSearchResult.Result.Add(new EditSearchResultItem(item, LanguageHelper.GetText("properties.mapobjectid"), item.MapObjectID, EditSearchResultMatchType.Full));
			}
			else if (item.MapObjectID.IndexOf(searchValue, StringComparison.InvariantCultureIgnoreCase) >= 0)
			{
				editSearchResult.Result.Add(new EditSearchResultItem(item, LanguageHelper.GetText("properties.mapobjectid"), item.MapObjectID, EditSearchResultMatchType.Partially));
			}
			foreach (ObjectPropertyInstance item2 in item.Properties.Items)
			{
				try
				{
					if (item2.Base.PropertyClass == ObjectPropertyClass.CustomHandling || item2.Base.AppearanceType == ObjectPropertyAppearance.Hidden || (item2.Base.AllowedValues != null && item2.Base.AllowedValues.Count > 0))
					{
						continue;
					}
					if (item2.Base.PropertyClass == ObjectPropertyClass.TargetObjectDataMultiple)
					{
						if (result == 0 || !(item2.Value is string))
						{
							continue;
						}
						int[] array = Converter.StringToIntArray((string)item2.Value);
						for (int i = 0; i < array.Length; i++)
						{
							if (array[i] == result)
							{
								editSearchResult.Result.Add(new EditSearchResultItem(item, item2.Base.Name, string.Join(", ", array), EditSearchResultMatchType.Full));
								break;
							}
						}
						continue;
					}
					if (item2.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
					{
						if (result != 0 && (int)item2.Value == result)
						{
							editSearchResult.Result.Add(new EditSearchResultItem(item, item2.Base.Name, result.ToString(), EditSearchResultMatchType.Full));
						}
					}
					else if (item2.Value is string)
					{
						if (string.Compare((string)item2.Value, searchValue, ignoreCase: true) == 0)
						{
							editSearchResult.Result.Add(new EditSearchResultItem(item, item2.Base.Name, (string)item2.Value, EditSearchResultMatchType.Full));
						}
						else if (((string)item2.Value).IndexOf(searchValue, StringComparison.InvariantCultureIgnoreCase) >= 0)
						{
							editSearchResult.Result.Add(new EditSearchResultItem(item, item2.Base.Name, (string)item2.Value, EditSearchResultMatchType.Partially));
						}
					}
					else if (item2.Value is int && result != 0 && (int)item2.Value == result)
					{
						editSearchResult.Result.Add(new EditSearchResultItem(item, item2.Base.Name, result.ToString(), EditSearchResultMatchType.Full));
					}
				}
				catch (Exception ex)
				{
					ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Search error: " + ex.Message);
				}
			}
		}
		return editSearchResult;
	}

	public void EditForceSelectObjects(List<ObjectData> items)
	{
		if (items == null || items.Count <= 0)
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 zero = Microsoft.Xna.Framework.Vector2.Zero;
		EditSelectedObjects.Clear();
		foreach (ObjectData item in items)
		{
			if (item != null && !item.IsDisposed)
			{
				EditUnlockShowLayer(item);
				EditSelectedObjects.Add(item);
				zero += item.GetWorldCenterPosition();
			}
		}
		ushort num = 0;
		List<ObjectData> list = new List<ObjectData>();
		if (EditSelectedObjects.Count > 0)
		{
			num = EditSelectedObjects[0].GroupID;
			for (int num2 = EditSelectedObjects.Count - 1; num2 >= 0; num2--)
			{
				if (EditSelectedObjects[num2].GroupID != num)
				{
					EditSelectedObjects.RemoveAt(num2);
				}
			}
			list.AddRange(EditSelectedObjects);
			Camera.SetPositionAndZoom(EditSelectedObjects[0].GetWorldCenterPosition(), Math.Max(2f, Math.Min(Camera.Zoom, 6f)));
		}
		EditAutoCloseEditGroupOnSelectionChanged();
		if (num != 0)
		{
			EditEnterGroupEdit(num);
			EditSelectedObjects.Clear();
			EditSelectedObjects.AddRange(list);
		}
		EditAfterSelection();
	}

	public void EditFlipSelectedObjectsH()
	{
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		EditRemovePreviewObjects();
		EditHistoryGroupBegin();
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(EditGetCenterOfSelection());
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject);
			Microsoft.Xna.Framework.Vector2 worldPosition = editSelectedObject.GetWorldPosition();
			worldPosition.X += (vector.X - worldPosition.X) * 2f;
			editSelectedObject.Body.SetTransform(Converter.WorldToBox2D(worldPosition), 0f - editSelectedObject.GetAngle());
			editSelectedObject.EditFlipObject((short)(-editSelectedObject.FaceDirection));
			editSelectedObject.Body.SetTransform(editSelectedObject.Body.GetWorldPoint(new Microsoft.Xna.Framework.Vector2((0f - editSelectedObject.LocalBox2DBodyCenterPoint.X) * 2f, 0f)), editSelectedObject.Body.GetAngle());
			EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject);
			EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.Flip, dataBefore, dataAfter));
		}
		EditPlacePreviewObjects();
		EditHistoryGroupEnd();
		EditAfterSelection();
	}

	public void EditFlipSelectedObjectsV()
	{
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		EditRemovePreviewObjects();
		EditHistoryGroupBegin();
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(EditGetCenterOfSelection());
		float num = (float)Math.PI;
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject);
			Microsoft.Xna.Framework.Vector2 worldPosition = editSelectedObject.GetWorldPosition();
			worldPosition.Y += (vector.Y - worldPosition.Y) * 2f;
			editSelectedObject.Body.SetTransform(Converter.WorldToBox2D(worldPosition), 0f - editSelectedObject.GetAngle() + num);
			editSelectedObject.EditFlipObject((short)(-editSelectedObject.FaceDirection));
			editSelectedObject.Body.SetTransform(editSelectedObject.Body.GetWorldPoint(new Microsoft.Xna.Framework.Vector2((0f - editSelectedObject.LocalBox2DBodyCenterPoint.X) * 2f, 0f)), editSelectedObject.Body.GetAngle());
			EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject);
			EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.Flip, dataBefore, dataAfter));
		}
		EditPlacePreviewObjects();
		EditHistoryGroupEnd();
		EditAfterSelection();
	}

	public ushort EditGetFreeGroupIDCount()
	{
		HashSet<ushort> hashSet = new HashSet<ushort>();
		Dictionary<int, ObjectData>[] array = new Dictionary<int, ObjectData>[2] { StaticObjects, DynamicObjects };
		for (int i = 0; i < array.Length; i++)
		{
			foreach (KeyValuePair<int, ObjectData> item in array[i])
			{
				ObjectData value = item.Value;
				hashSet.Add(value.GroupID);
			}
		}
		if (65000 >= hashSet.Count)
		{
			return (ushort)(65000 - hashSet.Count);
		}
		return 0;
	}

	public ushort EditGetFreeGroupID()
	{
		ushort num = 0;
		HashSet<ushort> hashSet = new HashSet<ushort>();
		Dictionary<int, ObjectData>[] array = new Dictionary<int, ObjectData>[2] { StaticObjects, DynamicObjects };
		for (int i = 0; i < array.Length; i++)
		{
			foreach (KeyValuePair<int, ObjectData> item in array[i])
			{
				ObjectData value = item.Value;
				hashSet.Add(value.GroupID);
			}
		}
		List<ushort> list = new List<ushort>(hashSet);
		list.Sort();
		foreach (ushort item2 in list)
		{
			if (item2 <= 0 || item2 - 1 <= num)
			{
				num = item2;
				continue;
			}
			return (ushort)(num + 1);
		}
		if (num < 65000)
		{
			return (ushort)(num + 1);
		}
		return 0;
	}

	public LayerStatus EditGetLayerStatus(ObjectData objectData)
	{
		return EditGetLayerStatus(objectData.LocalDrawCategory, objectData.LocalRenderLayer);
	}

	public LayerStatus EditGetLayerStatus(int categoryIndex, int layerIndex)
	{
		Layers<ObjectData> layers = RenderCategories[categoryIndex];
		if (layerIndex < layers.TotalLayers)
		{
			Layer<ObjectData> layer = layers.GetLayer(layerIndex);
			return new LayerStatus(layer.IsLocked, layer.IsVisible);
		}
		return new LayerStatus(isLocked: false, isVisisble: true);
	}

	public void EditUnlockShowLayer(ObjectData od)
	{
		Layers<ObjectData> layers = RenderCategories[od.LocalDrawCategory];
		Layer<ObjectData> layer = ((od.LocalRenderLayer < layers.TotalLayers) ? layers.GetLayer(od.LocalRenderLayer) : null);
		if (layer != null && (layer.IsLocked | !layer.IsVisible))
		{
			layer.IsLocked = false;
			layer.IsVisible = true;
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, od.LocalDrawCategory, od.LocalRenderLayer, layer);
		}
	}

	public void EditSelectedObjectsHandlePostAdd(ObjectData addedObject, EditSelectedObjectsOptions options)
	{
		EditSelectedObjectsHandlePostAdd(new ObjectData[1] { addedObject }, options);
	}

	public void EditSelectedObjectsHandlePostAdd(IEnumerable<ObjectData> addedObjects, EditSelectedObjectsOptions options)
	{
		if ((options & EditSelectedObjectsOptions.GroupCheck) != EditSelectedObjectsOptions.GroupCheck || EditGroupID != 0)
		{
			return;
		}
		Dictionary<ushort, EditSelectionGroupInfo> dictionary = new Dictionary<ushort, EditSelectionGroupInfo>();
		foreach (ObjectData addedObject in addedObjects)
		{
			if (addedObject.GroupID <= 0 || dictionary.ContainsKey(addedObject.GroupID))
			{
				continue;
			}
			EditSelectionGroupInfo editSelectionGroupInfo = new EditSelectionGroupInfo();
			foreach (ObjectData item in GetObjectDataByGroupID(addedObject.GroupID))
			{
				editSelectionGroupInfo.AddObject(item, EditGetLayerStatus(item));
			}
			dictionary.Add(addedObject.GroupID, editSelectionGroupInfo);
		}
		if (dictionary.Count <= 0)
		{
			return;
		}
		List<ObjectData> list = new List<ObjectData>();
		List<ObjectData> list2 = new List<ObjectData>();
		foreach (KeyValuePair<ushort, EditSelectionGroupInfo> item2 in dictionary)
		{
			_ = item2.Key;
			EditSelectionGroupInfo value = item2.Value;
			List<ObjectData> allObjects = value.GetAllObjects();
			if (value.ContainsLockedOrHiddenLayer())
			{
				list.AddRange(allObjects);
			}
			list2.AddRange(allObjects);
		}
		if (list.Count > 0)
		{
			if (!EditCheckLayersEnabledForEdit(list, EditCheckLayersEnabledForEditMode.GroupSelection))
			{
				foreach (ObjectData item3 in list)
				{
					EditSelectedObjects.Remove(item3);
				}
				list2.Clear();
			}
			else
			{
				foreach (ObjectData item4 in list)
				{
					EditUnlockShowLayer(item4);
				}
			}
		}
		foreach (ObjectData item5 in list2)
		{
			if (!EditSelectedObjects.Contains(item5))
			{
				EditSelectedObjects.Add(item5);
			}
		}
	}

	public void EditSelectedObjectsHandlePostRemove(ObjectData removedObject, EditSelectedObjectsOptions options)
	{
		EditSelectedObjectsHandlePostRemove(new ObjectData[1] { removedObject }, options);
	}

	public void EditSelectedObjectsHandlePostRemove(IEnumerable<ObjectData> removedObjects, EditSelectedObjectsOptions options)
	{
		if ((options & EditSelectedObjectsOptions.GroupCheck) != EditSelectedObjectsOptions.GroupCheck || EditGroupID != 0)
		{
			return;
		}
		HashSet<ushort> hashSet = EditGetGroupIDsFromObjects(removedObjects);
		if (hashSet.Count <= 0)
		{
			return;
		}
		for (int num = EditSelectedObjects.Count - 1; num >= 0; num--)
		{
			if (hashSet.Contains(EditSelectedObjects[num].GroupID))
			{
				EditSelectedObjects.RemoveAt(num);
			}
		}
	}

	public bool EditCheckFreeNewGroupIDs()
	{
		if (EditGroupID > 0)
		{
			return true;
		}
		HashSet<ushort> hashSet = EditGetGroupIDsFromObjects(EditSelectedObjects);
		if (hashSet.Count > 0)
		{
			if (hashSet.Count > EditGetFreeGroupIDCount())
			{
				MessageBox.Show(LanguageHelper.GetText("mapEditor.grouping.groupLimitReached.infoMessage", ((ushort)65000).ToString()), LanguageHelper.GetText("error"), MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return false;
			}
			foreach (ushort item in hashSet)
			{
				foreach (ObjectData item2 in GetObjectDataByGroupID(item))
				{
					if (!EditSelectedObjects.Contains(item2))
					{
						EditSelectedObjects.Add(item2);
					}
				}
			}
		}
		return true;
	}

	public HashSet<ushort> EditGetGroupIDsFromObjects(IEnumerable<ObjectData> objects, bool includeUngrouped = false)
	{
		HashSet<ushort> hashSet = new HashSet<ushort>();
		foreach (ObjectData @object in objects)
		{
			if (includeUngrouped || @object.GroupID > 0)
			{
				hashSet.Add(@object.GroupID);
			}
		}
		return hashSet;
	}

	public void EditEnterGroupEdit()
	{
		ushort num = 0;
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (editSelectedObject.GroupID > 0)
			{
				num = editSelectedObject.GroupID;
				break;
			}
		}
		if (num > 0)
		{
			EditEnterGroupEdit(num);
		}
	}

	public void EditEnterGroupEdit(ushort groupID)
	{
		ConsoleOutput.ShowMessage(ConsoleOutputType.Information, $"Entering group edit mode for group {groupID}");
		EditCancelMouseActions();
		GroupInfo groupInfo = GetGroupInfo(groupID);
		if (groupInfo == null)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "No group found. Closing group edit");
			EditCloseGroupEdit();
			return;
		}
		EditGroupID = groupID;
		m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.UpdateGroupEditLabel, true, groupInfo.GroupLabel);
		EditSelectGroup(EditGroupID);
	}

	public void EditCloseGroupEdit(bool selectGroup = true)
	{
		ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Closing group edit mode");
		if (EditGroupID > 0 && selectGroup)
		{
			EditSelectGroup(EditGroupID);
		}
		EditGroupID = 0;
		m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.UpdateGroupEditLabel, false, "");
	}

	public void EditSelectGroup(ushort groupID)
	{
		GroupInfo groupInfo = GetGroupInfo(EditGroupID);
		EditSelectedObjects.Clear();
		if (groupInfo != null)
		{
			if (groupInfo.Marker != null)
			{
				EditSelectedObjects.Add(groupInfo.Marker);
			}
			EditSelectedObjects.AddRange(groupInfo.Objects);
		}
	}

	public void EditMergeSelectedObjectsIntoGroup(ushort mergeGroupID)
	{
		if (!Enumerable.Contains(EditGetSelectedGroupIDs(), mergeGroupID))
		{
			return;
		}
		EditHistoryGroupBegin();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (editSelectedObject.GroupID != mergeGroupID && !(editSelectedObject is ObjectGroupMarker))
			{
				EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject);
				editSelectedObject.SetGroupID(mergeGroupID);
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject);
				EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.GroupChange, dataBefore, dataAfter));
			}
		}
		EditHistoryGroupEnd();
		EditAfterSelection();
	}

	public void EditGroupSelectedObjects()
	{
		if (!EditCheckCanGroupSelectedObjectsIntoNewGroup())
		{
			ushort[] array = EditGetSelectedGroupIDs();
			if (array.Length == 1 && EditCheckUngroupedObjectsInSelection())
			{
				EditMergeSelectedObjectsIntoGroup(array[0]);
			}
			return;
		}
		if (EditCheckGroupMarkerInSelection())
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Grouping performed while having a GroupMarker with GroupID=0 selected");
			MessageBox.Show(LanguageHelper.GetText("mapEditor.grouping.anotherGroupAlreadySelected.infoMessage", ((ushort)65000).ToString()), LanguageHelper.GetText("error"), MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		Tile tile = TileDatabase.Get("GroupMarker");
		int num = EditGetLastSelectedLayer(tile.DrawCategory);
		if (RenderCategories[tile.DrawCategory].TotalLayers > 0)
		{
			Layer<ObjectData> layer = RenderCategories[tile.DrawCategory].GetLayer(num);
			if (layer.IsLocked || !layer.IsVisible)
			{
				if (MessageBox.Show(LanguageHelper.GetText("mapEditor.grouping.groupLayerLockedHidden.askToUnlockShow", layer.Name), LanguageHelper.GetText("mapEditor.layerLockedHidden"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
				{
					return;
				}
				layer.IsLocked = false;
				layer.IsVisible = true;
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSync, tile.DrawCategory, num, layer);
			}
		}
		ushort num2 = EditGetFreeGroupID();
		if (num2 == 0)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Grouping limit reached");
			MessageBox.Show(LanguageHelper.GetText("mapEditor.grouping.groupLimitReached.infoMessage", ((ushort)65000).ToString()), LanguageHelper.GetText("error"), MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		List<ObjectData> list = new List<ObjectData>(EditSelectedObjects);
		EditHistoryGroupBegin();
		ObjectData objectData = EditCreatePreviewObjectSingle("GroupMarker", Converter.Box2DToWorld(EditGetCenterOfSelection()), 0f, 1);
		objectData.SetGroupID(num2);
		if (EditSnapToGrid)
		{
			Microsoft.Xna.Framework.Vector2 worldCoordinate = EditSnapPositionToGrid(objectData.GetWorldPosition(), objectData, snapX: true, snapY: true);
			objectData.Body.SetTransform(Converter.ConvertWorldToBox2D(worldCoordinate), objectData.Body.GetAngle());
		}
		EditPlacePreviewObjects();
		foreach (ObjectData item in list)
		{
			EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(item);
			item.SetGroupID(num2);
			EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(item);
			EditHistoryAddEntry(new EditHistoryItemObject(item, EditHistoryObjectAction.GroupChange, dataBefore, dataAfter));
		}
		EditSelectedObjects.AddRange(list);
		EditSelectedObjectsHandlePostAdd(objectData, EditSelectedObjectsOptions.None);
		EditHistoryGroupEnd();
		EditAfterSelection();
	}

	public void EditUngroupSelectedObjectsInGroupEdit()
	{
		if (EditGroupID <= 0)
		{
			return;
		}
		HashSet<int> ungroupedObjectIDs = new HashSet<int>();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (!editSelectedObject.IsGroupMarker)
			{
				if (ungroupedObjectIDs.Count == 0)
				{
					EditHistoryGroupBegin();
				}
				ungroupedObjectIDs.Add(editSelectedObject.ObjectID);
				EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject);
				editSelectedObject.SetGroupID(0);
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject);
				EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.GroupChange, dataBefore, dataAfter));
			}
		}
		if (ungroupedObjectIDs.Count > 0)
		{
			EditHistoryGroupEnd();
			EditSelectedObjects.RemoveAll((ObjectData x) => ungroupedObjectIDs.Contains(x.ObjectID));
			EditAfterSelection();
		}
	}

	public void EditUngroupSelectedObjects()
	{
		if (EditGroupID > 0)
		{
			EditUngroupSelectedObjectsInGroupEdit();
		}
		else
		{
			if (!EditCheckCanUngroupSelectedObjects())
			{
				return;
			}
			EditHistoryGroupBegin();
			foreach (ushort item in EditGetGroupIDsFromObjects(EditSelectedObjects))
			{
				foreach (ObjectData item2 in GetObjectDataByGroupID(item))
				{
					EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(item2);
					item2.SetGroupID(0);
					EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(item2);
					EditHistoryAddEntry(new EditHistoryItemObject(item2, EditHistoryObjectAction.GroupChange, dataBefore, dataAfter));
					if (item2.IsGroupMarker)
					{
						dataBefore = new EditHistoryItemObjectData(item2);
						EditHistoryAddEntry(new EditHistoryItemObject(item2, EditHistoryObjectAction.Deletion, dataBefore, null));
						item2.Remove();
						EditSelectedObjects.Remove(item2);
					}
				}
			}
			EditHistoryGroupEnd();
			EditAfterSelection();
		}
	}

	public void EditSetSelectedColor(int level, string colorName)
	{
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		bool flag = false;
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (editSelectedObject.Colors[level] != colorName && editSelectedObject.CanSetColor(level, colorName))
			{
				flag = true;
				break;
			}
		}
		if (!flag)
		{
			return;
		}
		EditHistoryGroupBegin();
		foreach (ObjectData editSelectedObject2 in EditSelectedObjects)
		{
			if (editSelectedObject2.CanSetColor(level, colorName))
			{
				EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject2);
				editSelectedObject2.ApplyColor(level, colorName);
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject2);
				EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject2, EditHistoryObjectAction.ColorValueChange, dataBefore, dataAfter));
			}
		}
		EditHistoryGroupEnd();
	}

	public void EditMoveSelectedObjectsInCategoryToLayer(int localDrawCategory, int layer)
	{
		bool flag = false;
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (editSelectedObject.LocalDrawCategory == localDrawCategory && editSelectedObject.LocalRenderLayer != layer)
			{
				if (!flag)
				{
					EditHistoryGroupBegin();
					flag = true;
				}
				EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject);
				editSelectedObject.RemoveFromRenderLayer();
				editSelectedObject.AddToRenderLayer(layer, localDrawCategory);
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject);
				EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.LayerChange, dataBefore, dataAfter));
			}
		}
		if (flag)
		{
			EditHistoryGroupEnd();
		}
	}

	public void EditShiftOrderSelectedObjects(EditShiftOrder order)
	{
		EditShiftOrderSelectedObjects(order, EditSelectedObjects);
	}

	public void EditShiftOrderSelectedObjects(EditShiftOrder order, List<ObjectData> objectsToReorder)
	{
		if (objectsToReorder.Count <= 0)
		{
			return;
		}
		EditHistoryGroupBegin();
		SFDRenderCategories<ObjectData> sFDRenderCategories = RenderCategories.CopyRenderCategoriesWithSelection(objectsToReorder);
		for (int i = 0; i < RenderCategories.Length; i++)
		{
			if (sFDRenderCategories[i].TotalItems <= 0)
			{
				continue;
			}
			for (int j = 0; j < sFDRenderCategories[i].TotalLayers; j++)
			{
				List<ObjectData> items = sFDRenderCategories[i].GetLayer(j).Items;
				if (items.Count <= 0)
				{
					continue;
				}
				List<ObjectData> items2 = RenderCategories[i].GetLayer(j).Items;
				switch (order)
				{
				case EditShiftOrder.Top:
					foreach (ObjectData item in items)
					{
						EditHistoryItemObjectData dataBefore4 = new EditHistoryItemObjectData(item);
						item.SetZOrder(items2.Count - 1);
						EditHistoryItemObjectData dataAfter4 = new EditHistoryItemObjectData(item);
						EditHistoryAddEntry(new EditHistoryItemObject(item, EditHistoryObjectAction.ChangeZOrder, dataBefore4, dataAfter4));
					}
					break;
				case EditShiftOrder.Bottom:
				{
					for (int num3 = items.Count - 1; num3 >= 0; num3--)
					{
						ObjectData objectData3 = items[num3];
						EditHistoryItemObjectData dataBefore2 = new EditHistoryItemObjectData(objectData3);
						objectData3.SetZOrder(0);
						EditHistoryItemObjectData dataAfter2 = new EditHistoryItemObjectData(objectData3);
						EditHistoryAddEntry(new EditHistoryItemObject(objectData3, EditHistoryObjectAction.ChangeZOrder, dataBefore2, dataAfter2));
					}
					break;
				}
				case EditShiftOrder.Down:
				{
					for (int k = 0; k < items.Count; k++)
					{
						int num4 = items2.IndexOf(items[k]);
						if (num4 > 0)
						{
							if (!items.Contains(items2[num4 - 1]))
							{
								ObjectData objectData4 = items2[num4];
								EditHistoryItemObjectData dataBefore3 = new EditHistoryItemObjectData(objectData4);
								objectData4.SetZOrder(num4 - 1);
								EditHistoryItemObjectData dataAfter3 = new EditHistoryItemObjectData(objectData4);
								EditHistoryAddEntry(new EditHistoryItemObject(objectData4, EditHistoryObjectAction.ChangeZOrder, dataBefore3, dataAfter3));
							}
							else
							{
								ObjectData objectData5 = items2[num4];
								EditHistoryItemObjectData editHistoryItemObjectData2 = new EditHistoryItemObjectData(objectData5);
								objectData5.SetZOrder(num4);
								EditHistoryAddEntry(new EditHistoryItemObject(objectData5, EditHistoryObjectAction.ChangeZOrder, editHistoryItemObjectData2, editHistoryItemObjectData2));
							}
						}
					}
					break;
				}
				case EditShiftOrder.Up:
				{
					for (int num = items.Count - 1; num >= 0; num--)
					{
						int num2 = items2.IndexOf(items[num]);
						if (num2 < items2.Count - 1)
						{
							if (!items.Contains(items2[num2 + 1]))
							{
								ObjectData objectData = items2[num2];
								EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(objectData);
								objectData.SetZOrder(num2 + 1);
								EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(objectData);
								EditHistoryAddEntry(new EditHistoryItemObject(objectData, EditHistoryObjectAction.ChangeZOrder, dataBefore, dataAfter));
							}
							else
							{
								ObjectData objectData2 = items2[num2];
								EditHistoryItemObjectData editHistoryItemObjectData = new EditHistoryItemObjectData(objectData2);
								objectData2.SetZOrder(num2);
								EditHistoryAddEntry(new EditHistoryItemObject(objectData2, EditHistoryObjectAction.ChangeZOrder, editHistoryItemObjectData, editHistoryItemObjectData));
							}
						}
					}
					break;
				}
				}
			}
		}
		EditHistoryGroupEnd();
	}

	public void EditCancelMouseActions()
	{
		Microsoft.Xna.Framework.Vector2 mouseWorldPosition = GetMouseWorldPosition();
		if (EditSelectedObjects.Count == m_editSelectionPositionBeforeMove.Count)
		{
			EditMouseLeftMoveEnd(mouseWorldPosition);
		}
		EditMouseRightMoveEnd(mouseWorldPosition);
		StateEditor.m_mouseLeftPerformingMove = false;
		StateEditor.m_mouseRightPerformingMove = false;
		m_editLeftMoveAction = EditLeftMoveAction.None;
		m_editSelectionPositionBeforeMove.Clear();
		m_editRotationInAction = false;
		m_editRotationStatusesBeforeRotate.Clear();
	}

	public void EditAutoSelectGroupedObjects(List<ObjectData> objects)
	{
		if (EditGroupID > 0)
		{
			return;
		}
		if (objects == null)
		{
			objects = EditSelectedObjects;
		}
		HashSet<ushort> hashSet = EditGetGroupIDsFromObjects(objects);
		if (hashSet.Count <= 0)
		{
			return;
		}
		foreach (ushort item in hashSet)
		{
			foreach (ObjectData item2 in GetObjectDataByGroupID(item))
			{
				if (!item2.TerminationInitiated && !objects.Contains(item2))
				{
					objects.Add(item2);
				}
			}
		}
	}

	public void EditAutoCloseEditGroupOnSelectionChanged()
	{
		if (EditGroupID == 0)
		{
			return;
		}
		if (EditSelectedObjects.Count == 0)
		{
			GroupInfo groupInfo = GetGroupInfo(EditGroupID);
			if (groupInfo == null || groupInfo.Marker == null || groupInfo.Marker.TerminationInitiated)
			{
				EditCloseGroupEdit(selectGroup: false);
			}
			return;
		}
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (editSelectedObject.GroupID != EditGroupID)
			{
				EditCloseGroupEdit(selectGroup: false);
				break;
			}
		}
	}

	public void EditCopySelectedObjects()
	{
		EditAutoSelectGroupedObjects(EditSelectedObjects);
		List<ObjectData> collection = new List<ObjectData>(EditSelectedObjects);
		if (EditGroupID > 0)
		{
			EditRemoveGroupMarkerFromSelection();
		}
		int[] array = EditSortSelectedObjectsByZOrder();
		EditCopyOffsetPositions.Clear();
		for (int i = 0; i < array.Length; i++)
		{
			ObjectData objectData = EditSelectedObjects[array[i]];
			EditCopyOffsetPositions.Add(objectData.GetWorldPosition() - EditSelectedObjects[array[0]].GetWorldPosition());
		}
		EditCopyObjects.Clear();
		EditCopyObjectsInEditGroupID = EditGroupID;
		for (int j = 0; j < array.Length; j++)
		{
			ObjectData objectData2 = EditSelectedObjects[array[j]];
			object[] item = new object[8]
			{
				objectData2.MapObjectID,
				objectData2.GetWorldPosition(),
				objectData2.GetAngle(),
				objectData2.Colors,
				objectData2.Properties.ToObjectArray(),
				objectData2.FaceDirection,
				objectData2.ObjectID,
				objectData2.GroupID
			};
			EditCopyObjects.Add(item);
		}
		EditSelectedObjects.Clear();
		EditSelectedObjects.AddRange(collection);
	}

	public void EditPasteCopiedObjects(bool samePosition = false)
	{
		if (EditCopyObjects.Count <= 0)
		{
			return;
		}
		if (EditGroupID > 0)
		{
			EditRemoveGroupMarkerFromCopyObjects();
			if (EditCopyObjects.Count == 0)
			{
				return;
			}
		}
		else if (EditCopyObjectsInEditGroupID > 0)
		{
			EditRemoveGroupMarkerFromCopyObjects();
		}
		Dictionary<ushort, ushort> dictionary = new Dictionary<ushort, ushort>();
		if (EditGroupID == 0)
		{
			HashSet<ushort> hashSet = new HashSet<ushort>();
			foreach (object[] editCopyObject in EditCopyObjects)
			{
				ushort num = (ushort)editCopyObject[7];
				if (num > 0)
				{
					hashSet.Add(num);
				}
			}
			if (hashSet.Count > 0 && hashSet.Count > EditGetFreeGroupIDCount())
			{
				MessageBox.Show(LanguageHelper.GetText("mapEditor.grouping.groupLimitReached.infoMessage", ((ushort)65000).ToString()), LanguageHelper.GetText("error"), MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
		}
		EditPreviewObjectsHistoryEntries = new List<EditHistoryItemObject>();
		object[] array = EditCopyObjects[0];
		ObjectData objectData = EditCreatePreviewObject((string)array[0], Camera.Position, 0f, (short)array[5]);
		Microsoft.Xna.Framework.Vector2 worldCoordinate = EditSnapPositionToGrid(objectData.GetWorldPosition(), objectData, snapX: true, snapY: true);
		if (samePosition)
		{
			worldCoordinate = (Microsoft.Xna.Framework.Vector2)array[1];
			worldCoordinate.X = (float)Math.Round(worldCoordinate.X, 1);
			worldCoordinate.Y = (float)Math.Round(worldCoordinate.Y, 1);
		}
		objectData.Body.SetTransform(Converter.ConvertWorldToBox2D(worldCoordinate), (float)array[2]);
		objectData.Properties.FromObjectArray((object[])array[4]);
		objectData.ApplyColors((string[])array[3]);
		if ((ushort)array[7] > 0)
		{
			if (EditGroupID > 0)
			{
				objectData.SetGroupID(EditGroupID);
			}
			else if (EditCopyObjectsInEditGroupID > 0)
			{
				objectData.SetGroupID(0);
			}
			else
			{
				if (!dictionary.ContainsKey((ushort)array[7]))
				{
					dictionary.Add((ushort)array[7], EditGetFreeGroupID());
				}
				objectData.SetGroupID(dictionary[(ushort)array[7]]);
			}
		}
		List<ObjectData> list = new List<ObjectData>();
		list.Add(objectData);
		Dictionary<int, int> dictionary2 = new Dictionary<int, int>();
		dictionary2.Add((int)array[6], objectData.ObjectID);
		for (int i = 1; i < EditCopyObjects.Count; i++)
		{
			array = EditCopyObjects[i];
			ObjectData objectData2 = EditCreatePreviewObject((string)array[0], objectData.GetWorldPosition() + EditCopyOffsetPositions[i], 0f, (short)array[5]);
			Microsoft.Xna.Framework.Vector2 position = objectData2.Body.Position;
			if (samePosition)
			{
				position = (Microsoft.Xna.Framework.Vector2)array[1];
				position.X = (float)Math.Round(position.X, 1);
				position.Y = (float)Math.Round(position.Y, 1);
				position = Converter.WorldToBox2D(position);
			}
			objectData2.Body.SetTransform(position, (float)array[2]);
			objectData2.Properties.FromObjectArray((object[])array[4]);
			objectData2.ApplyColors((string[])array[3]);
			if ((ushort)array[7] > 0)
			{
				if (EditGroupID > 0)
				{
					objectData2.SetGroupID(EditGroupID);
				}
				else if (EditCopyObjectsInEditGroupID > 0)
				{
					objectData2.SetGroupID(0);
				}
				else
				{
					if (!dictionary.ContainsKey((ushort)array[7]))
					{
						dictionary.Add((ushort)array[7], EditGetFreeGroupID());
					}
					objectData2.SetGroupID(dictionary[(ushort)array[7]]);
				}
			}
			list.Add(objectData2);
			dictionary2.Add((int)array[6], objectData2.ObjectID);
		}
		EditProcessNewIDs(list, dictionary2);
		for (int j = 0; j < list.Count; j++)
		{
			list[j].Properties.CallChangedEvents();
		}
		foreach (ObjectData item in list)
		{
			EditPreviewObjectsHistoryEntries.Add(new EditHistoryItemObject(item, EditHistoryObjectAction.Creation, null, new EditHistoryItemObjectData(item)));
		}
		EditPlacePreviewObjects();
	}

	public void EditProcessNewIDs(List<ObjectData> objects, Dictionary<int, int> oldToNewValues)
	{
		Dictionary<ObjectPropertyInstance, object> dictionary = new Dictionary<ObjectPropertyInstance, object>();
		for (int i = 0; i < objects.Count; i++)
		{
			objects[i].ObjectIDCompression(oldToNewValues, dictionary);
		}
		dictionary.Clear();
		dictionary = null;
		for (int j = 0; j < objects.Count; j++)
		{
			objects[j].FinalizeProperties();
		}
	}

	public void EditCutSelectedObjects()
	{
		EditCopySelectedObjects();
		EditRemoveSelectedObjects();
	}

	public int[] EditSortSelectedObjectsByZOrder()
	{
		SFDRenderCategories<ObjectData> sFDRenderCategories = RenderCategories.CopyRenderCategoriesWithSelection(EditSelectedObjects);
		int[] array = new int[EditSelectedObjects.Count];
		for (int i = 0; i < array.Length; i++)
		{
			int indexOfItem = sFDRenderCategories.GetIndexOfItem(EditSelectedObjects[i], EditSelectedObjects[i].Tile.DrawCategory);
			array[indexOfItem] = i;
		}
		sFDRenderCategories.Clear();
		sFDRenderCategories = null;
		return array;
	}

	public void EditDuplicateSelectedObjects()
	{
		if (EditSelectedObjects.Count <= 0 || !EditCheckFreeNewGroupIDs())
		{
			return;
		}
		Dictionary<ushort, ushort> dictionary = new Dictionary<ushort, ushort>();
		EditSetPhysicsOff();
		List<ObjectData> list = new List<ObjectData>();
		if (EditGroupID > 0)
		{
			EditRemoveGroupMarkerFromSelection();
			if (EditSelectedObjects.Count == 0)
			{
				return;
			}
		}
		int[] array = EditSortSelectedObjectsByZOrder();
		int index = 0;
		Dictionary<int, int> dictionary2 = new Dictionary<int, int>();
		EditHistoryGroupBegin();
		for (int i = 0; i < array.Length; i++)
		{
			ObjectData objectData = EditSelectedObjects[array[i]];
			if (array[i] == 0)
			{
				index = i;
			}
			ObjectData objectData2 = BodyData.Read(CreateTile(objectData.MapObjectID, objectData.GetWorldPosition() + new Microsoft.Xna.Framework.Vector2(4f, -4f), objectData.GetAngle(), objectData.FaceDirection, Microsoft.Xna.Framework.Vector2.Zero, 0f)).Object;
			objectData2.Properties.SetValues(objectData.Properties.ToObjectArray());
			objectData2.ApplyColors(objectData.Colors);
			objectData2.SetRenderLayer(objectData.LocalRenderLayer);
			if (EditGroupID > 0)
			{
				objectData2.SetGroupID(objectData.GroupID);
			}
			else if (objectData.GroupID > 0)
			{
				if (!dictionary.ContainsKey(objectData.GroupID))
				{
					dictionary.Add(objectData.GroupID, EditGetFreeGroupID());
				}
				objectData2.SetGroupID(dictionary[objectData.GroupID]);
			}
			list.Add(objectData2);
			dictionary2.Add(objectData.ObjectID, objectData2.ObjectID);
		}
		EditProcessNewIDs(list, dictionary2);
		foreach (ObjectData item2 in list)
		{
			EditHistoryAddEntry(new EditHistoryItemObject(item2, EditHistoryObjectAction.Creation, null, new EditHistoryItemObjectData(item2)));
		}
		EditSelectedObjects.Clear();
		EditSelectedObjects.AddRange(list);
		EditSelectedObjectsHandlePostAdd(list, EditSelectedObjectsOptions.None);
		ObjectData item = EditSelectedObjects[index];
		EditSelectedObjects.Remove(item);
		EditSelectedObjects.Insert(0, item);
		array = null;
		list.Clear();
		list = null;
		EditHistoryGroupEnd();
		EditAfterSelection();
	}

	public void EditMoveSelectedObjects(int dirX, int dirY)
	{
		if (!m_editRotationInAction && EditSelectedObjects.Count > 0)
		{
			EditSetPhysicsOff();
			Microsoft.Xna.Framework.Vector2 worldPosition = EditSelectedObjects[0].GetWorldPosition();
			Microsoft.Xna.Framework.Vector2 vector = new Microsoft.Xna.Framework.Vector2(dirX, dirY);
			if (DoSnapToGrid())
			{
				vector *= (float)EditGridSize * 0.9f;
			}
			vector = EditSnapPositionToGrid(worldPosition + vector, EditSelectedObjects[0], dirX != 0, dirY != 0) - worldPosition;
			if (dirX != 0)
			{
				EditMoveSelectedObjects(dirX, 0, (int)Math.Round(Math.Abs(vector.X), 0, MidpointRounding.ToEven));
			}
			if (dirY != 0)
			{
				EditMoveSelectedObjects(0, dirY, (int)Math.Round(Math.Abs(vector.Y), 0, MidpointRounding.ToEven));
			}
		}
	}

	public void EditMoveSelectedObjects(int dirX, int dirY, int worldUnits)
	{
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		EditSetPhysicsOff();
		bool flag;
		if (!(flag = m_editLeftMoveAction == EditLeftMoveAction.Move))
		{
			EditHistoryGroupBegin();
		}
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(editSelectedObject);
			Microsoft.Xna.Framework.Vector2 worldPosition = editSelectedObject.GetWorldPosition();
			Microsoft.Xna.Framework.Vector2 vector = new Microsoft.Xna.Framework.Vector2(dirX, dirY);
			vector *= (float)worldUnits;
			editSelectedObject.Body.GetPosition();
			Microsoft.Xna.Framework.Vector2 position = Converter.ConvertWorldToBox2D(worldPosition + vector);
			editSelectedObject.Body.SetTransform(position, editSelectedObject.Body.GetAngle());
			if (!flag)
			{
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(editSelectedObject);
				EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.Movement, dataBefore, dataAfter));
			}
		}
		if (!flag)
		{
			EditHistoryGroupEnd();
		}
	}

	public void EditRemoveSelectedObjects()
	{
		if (m_editLeftMoveAction != EditLeftMoveAction.None || m_editRotationInAction)
		{
			return;
		}
		EditCancelCurrentAction();
		if (EditGroupID > 0)
		{
			EditRemoveGroupMarkerFromSelection();
		}
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		EditSetPhysicsOff();
		EditHistoryGroupBegin();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			EditHistoryAddEntry(new EditHistoryItemObject(editSelectedObject, EditHistoryObjectAction.Deletion, new EditHistoryItemObjectData(editSelectedObject), null));
			editSelectedObject.Remove();
		}
		EditHistoryGroupEnd();
		EditSelectedObjects.Clear();
		EditAfterSelection();
	}

	public void EditCtrlShortCommand(int commandNr)
	{
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		int num = commandNr - 1;
		if (num < 0)
		{
			return;
		}
		Type odType = EditSelectedObjects[0].GetType();
		if (!EditSelectedObjects.All((ObjectData x) => odType.Equals(x.GetType())))
		{
			return;
		}
		if (odType == typeof(ObjectPathNode))
		{
			ObjectPropertyItem property = ObjectProperties.GetProperty(278);
			if (num < property.AllowedValues.Count)
			{
				List<ObjectPropertyInstance> opis = EditSelectedObjects.Select((ObjectData x) => x.Properties.Get(ObjectPropertyID.ScriptPathNode_PathNodeType)).ToList();
				UpdateValueToProperties(opis, property.AllowedValues[num].Value);
				EditSetMapEditorPropertiesWindow();
			}
		}
		else
		{
			if (!(odType == typeof(ObjectPathNodeConnection)))
			{
				return;
			}
			ObjectPropertyItem property2 = ObjectProperties.GetProperty(279);
			if (num < property2.AllowedValues.Count)
			{
				List<ObjectPropertyInstance> opis2 = EditSelectedObjects.Select((ObjectData x) => x.Properties.Get(ObjectPropertyID.ScriptPathNodeConnection_PathNodeConnectionType)).ToList();
				UpdateValueToProperties(opis2, property2.AllowedValues[num].Value);
				EditSetMapEditorPropertiesWindow();
			}
		}
	}

	public void EditMouseLeftDoubleClick(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (!IsControlPressed() && !IsShiftPressed())
		{
			ObjectData objectData = EditCheckTouch(EditSelectedObjects, worldPosition);
			if (objectData != null && objectData.GroupID > 0)
			{
				EditEnterGroupEdit(objectData.GroupID);
			}
			else
			{
				EditCloseGroupEdit();
			}
		}
	}

	public void EditMouseLeftDown(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (m_editRotationInAction || m_editTargetObjectPropertyInstance != null)
		{
			return;
		}
		m_editMouseDownWorldPosition = worldPosition;
		if (EditSelectedObjects.Count == 1)
		{
			m_editResizeSizeable = EditCheckTouchSizeable(worldPosition);
			if (m_editResizeSizeable != Tile.SIZEABLE.N)
			{
				EditSetPhysicsOff();
				m_editLeftMoveAction = EditLeftMoveAction.Resize;
				m_editResizeDataBefore = new EditHistoryItemObjectData(EditSelectedObjects[0]);
				return;
			}
		}
		ObjectData objectData = EditCheckTouch(EditSelectedObjects, worldPosition);
		if (objectData != null)
		{
			EditSelectedObjects.Remove(objectData);
			EditSelectedObjects.Insert(0, objectData);
			m_editLeftMoveAction = EditLeftMoveAction.Move;
		}
		else
		{
			EditSelectionArea.Start = worldPosition;
			EditSelectionArea.End = worldPosition;
			m_editLeftMoveAction = EditLeftMoveAction.Select;
		}
	}

	public void EditMouseLeftMoveStart(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (EditSelectedObjects.Count <= 0 || m_editLeftMoveAction != EditLeftMoveAction.Move)
		{
			return;
		}
		EditSetPhysicsOff();
		m_editSelectionPositionBeforeMove.Clear();
		EditOffsetPositions.Clear();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			m_editSelectionPositionBeforeMove.Add(editSelectedObject.Body.GetPosition());
			EditOffsetPositions.Add(EditSelectedObjects[0].Body.GetPosition() - editSelectedObject.Body.GetPosition());
		}
		EditMouseOffset = Converter.ConvertBox2DToWorld(m_editSelectionPositionBeforeMove[0]) - worldPosition;
	}

	public void EditMouseLeftMove(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (m_editSelectionPositionBeforeMove.Count != EditSelectedObjects.Count && m_editLeftMoveAction == EditLeftMoveAction.Move)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "GameWorld.EditMouseLeftMove List counts not matching - converting to Selection");
			EditSelectionArea.Start = worldPosition;
			m_editLeftMoveAction = EditLeftMoveAction.Select;
		}
		if (EditSelectedObjects.Count > 0 && m_editLeftMoveAction == EditLeftMoveAction.Move && m_editSelectionPositionBeforeMove.Count == EditSelectedObjects.Count)
		{
			EditSetPhysicsOff();
			if (IsControlPressed())
			{
				for (int i = 0; i < EditSelectedObjects.Count; i++)
				{
					if (m_editSelectionPositionBeforeMove[i] != EditSelectedObjects[i].Body.GetPosition())
					{
						EditSelectedObjects[i].Body.SetTransform(m_editSelectionPositionBeforeMove[i], EditSelectedObjects[i].Body.GetAngle());
						if (EditSelectedObjects[i].Body.GetType() == Box2D.XNA.BodyType.Dynamic)
						{
							EditSelectedObjects[i].Body.SetAwake(flag: true);
						}
					}
				}
				if (EditPreviewObjects.Count <= 0)
				{
					if (!EditCheckFreeNewGroupIDs())
					{
						m_editLeftMoveAction = EditLeftMoveAction.None;
						return;
					}
					Dictionary<ushort, ushort> dictionary = new Dictionary<ushort, ushort>();
					ObjectData objectData = null;
					int[] array = EditSortSelectedObjectsByZOrder();
					bool flag = true;
					for (int j = 0; j < array.Length; j++)
					{
						if (array[j] < 0 || array[j] >= EditSelectedObjects.Count)
						{
							flag = false;
							ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "GameWorld.EditMouseLeftMove creationOrder index out of range");
							break;
						}
					}
					if (flag)
					{
						int num = -1;
						EditPreviewObjectsHistoryEntries = new List<EditHistoryItemObject>();
						Dictionary<int, int> dictionary2 = new Dictionary<int, int>();
						List<ObjectData> list = new List<ObjectData>();
						for (int k = 0; k < array.Length; k++)
						{
							ObjectData objectData2 = EditSelectedObjects[array[k]];
							if (EditGroupID > 0 && objectData2 is ObjectGroupMarker)
							{
								continue;
							}
							ObjectData objectData3 = EditCreatePreviewObject(objectData2, worldPosition - Converter.ConvertBox2DToWorld(EditOffsetPositions[array[k]]));
							list.Add(objectData3);
							if (EditGroupID == 0)
							{
								ushort groupID = EditSelectedObjects[array[k]].GroupID;
								if (groupID > 0)
								{
									if (!dictionary.ContainsKey(groupID))
									{
										dictionary.Add(groupID, EditGetFreeGroupID());
									}
									objectData3.SetGroupID(dictionary[groupID]);
								}
							}
							if (num == -1 || array[k] < num)
							{
								objectData = EditPreviewObjects[EditPreviewObjects.Count - 1];
								num = array[k];
							}
							dictionary2.Add(EditSelectedObjects[array[k]].ObjectID, objectData3.ObjectID);
						}
						EditProcessNewIDs(EditPreviewObjects, dictionary2);
						foreach (ObjectData item in list)
						{
							EditPreviewObjectsHistoryEntries.Add(new EditHistoryItemObject(item, EditHistoryObjectAction.Creation, null, new EditHistoryItemObjectData(item)));
						}
						if (objectData != null)
						{
							EditPreviewObjects.Remove(objectData);
							EditPreviewObjects.Insert(0, objectData);
						}
					}
				}
				EditUpdatePreviewObjects(worldPosition);
				return;
			}
			Microsoft.Xna.Framework.Vector2 worldCoordinate = EditSnapPositionToGrid(worldPosition + EditMouseOffset, EditSelectedObjects[0], snapX: true, snapY: true);
			for (int l = 0; l < EditSelectedObjects.Count; l++)
			{
				Microsoft.Xna.Framework.Vector2 position = Converter.ConvertWorldToBox2D(worldCoordinate) - EditOffsetPositions[l];
				EditSelectedObjects[l].Body.SetTransform(position, EditSelectedObjects[l].Body.GetAngle());
				if (EditSelectedObjects[l].Body.GetType() == Box2D.XNA.BodyType.Dynamic)
				{
					EditSelectedObjects[l].Body.SetAwake(flag: true);
				}
			}
			EditRemovePreviewObjects();
		}
		else if (m_editLeftMoveAction == EditLeftMoveAction.Select)
		{
			EditSelectionArea.End = worldPosition;
		}
		else
		{
			if (m_editLeftMoveAction != EditLeftMoveAction.Resize || EditSelectedObjects.Count != 1)
			{
				return;
			}
			ObjectData objectData4 = EditSelectedObjects[0];
			Microsoft.Xna.Framework.Vector2 position2 = worldPosition - objectData4.GetWorldPosition();
			SFDMath.RotatePosition(ref position2, 0f - objectData4.GetAngle(), out position2);
			switch (m_editResizeSizeable)
			{
			case Tile.SIZEABLE.H:
				position2.Y = 0f;
				break;
			case Tile.SIZEABLE.V:
				position2.X = 0f;
				break;
			}
			FixedArray2<int> textureSize = objectData4.GetTextureSize();
			int num2 = textureSize[0];
			int num3 = textureSize[1];
			if (m_editResizeSizeable == Tile.SIZEABLE.H || m_editResizeSizeable == Tile.SIZEABLE.D)
			{
				int num4 = (int)(position2.X + (float)num2) / num2;
				if (num4 < 1)
				{
					num4 = 1;
				}
				if (objectData4.Properties.Exists(ObjectPropertyID.Size_X))
				{
					objectData4.Properties.Get(ObjectPropertyID.Size_X).Value = num4;
				}
			}
			if (m_editResizeSizeable == Tile.SIZEABLE.V || m_editResizeSizeable == Tile.SIZEABLE.D)
			{
				int num5 = (int)(0f - position2.Y + (float)num3) / num3;
				if (num5 < 1)
				{
					num5 = 1;
				}
				if (objectData4.Properties.Exists(ObjectPropertyID.Size_Y))
				{
					objectData4.Properties.Get(ObjectPropertyID.Size_Y).Value = num5;
				}
			}
		}
	}

	public void EditMouseLeftMoveEnd(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (EditSelectedObjects.Count > 0 && m_editLeftMoveAction == EditLeftMoveAction.Move)
		{
			if (IsControlPressed())
			{
				if (EditPreviewObjects.Count > 0)
				{
					if ((EditPreviewObjects[0].Body.GetPosition() - EditSelectedObjects[0].Body.GetPosition()).Length() > 0.01f)
					{
						EditPlacePreviewObjects();
					}
					else
					{
						EditRemovePreviewObjects();
					}
				}
			}
			else
			{
				EditHistoryGroupBegin();
				for (int i = 0; i < EditSelectedObjects.Count; i++)
				{
					EditHistoryItemObjectData editHistoryItemObjectData = new EditHistoryItemObjectData(EditSelectedObjects[i]);
					editHistoryItemObjectData.Position = m_editSelectionPositionBeforeMove[i];
					EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(EditSelectedObjects[i]);
					EditHistoryAddEntry(new EditHistoryItemObject(EditSelectedObjects[i], EditHistoryObjectAction.Movement, editHistoryItemObjectData, dataAfter));
				}
				EditHistoryGroupEnd();
				m_editSelectionPositionBeforeMove.Clear();
			}
		}
		else if (m_editLeftMoveAction == EditLeftMoveAction.Select)
		{
			EditSelectionArea.End = worldPosition;
			EditSelectArea(EditSelectionArea);
		}
		else if (m_editLeftMoveAction == EditLeftMoveAction.Resize && EditSelectedObjects.Count == 1)
		{
			ObjectData od = EditSelectedObjects[0];
			EditHistoryItemObjectData editResizeDataBefore = m_editResizeDataBefore;
			EditHistoryItemObjectData dataAfter2 = new EditHistoryItemObjectData(od);
			EditHistoryAddEntry(new EditHistoryItemObject(od, EditHistoryObjectAction.PropertyValueChange, editResizeDataBefore, dataAfter2));
		}
		EditMouseOffset = Microsoft.Xna.Framework.Vector2.Zero;
		m_editLeftMoveAction = EditLeftMoveAction.None;
	}

	public void EditMouseMiddleDoubleClick(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (m_editLeftMoveAction != EditLeftMoveAction.None || m_editRotationInAction)
		{
			return;
		}
		EditSetPhysicsOff();
		if (!IsControlPressed())
		{
			EditSelectedObjects.Clear();
		}
		EditMouseOffset = Microsoft.Xna.Framework.Vector2.Zero;
		m_editLeftMoveAction = EditLeftMoveAction.None;
		ObjectData objectData = null;
		int num = RenderCategories.Length - 1;
		while (true)
		{
			if (num >= 0)
			{
				for (int num2 = RenderCategories[num].TotalLayers - 1; num2 >= 0; num2--)
				{
					Layer<ObjectData> layer = RenderCategories[num].GetLayer(num2);
					if (layer.IsVisible && !layer.IsLocked)
					{
						int num3 = layer.Items.Count - 1;
						while (num3 >= 0)
						{
							ObjectData objectData2 = layer.Items[num3];
							if (!EditCheckTouch(objectData2, worldPosition))
							{
								num3--;
								continue;
							}
							objectData = objectData2;
							num2 = -1;
							break;
						}
					}
				}
				if (objectData != null)
				{
					break;
				}
				num--;
				continue;
			}
			EditAfterSelection();
			return;
		}
		List<ObjectData> list = new List<ObjectData>();
		if (!EditSelectedObjects.Contains(objectData))
		{
			EditSelectedObjects.Insert(0, objectData);
			list = EditSelectedObjectsChangeInWindow(objectData.MapObjectID, EditChangeSelectionInWindowType.Add);
			list.Add(objectData);
			EditSelectedObjectsHandlePostAdd(list, EditSelectedObjectsOptions.GroupCheck);
		}
		else
		{
			EditSelectedObjects.Remove(objectData);
			list = EditSelectedObjectsChangeInWindow(objectData.MapObjectID, EditChangeSelectionInWindowType.Remove);
			list.Add(objectData);
			EditSelectedObjectsHandlePostRemove(list, EditSelectedObjectsOptions.GroupCheck);
		}
		EditAfterSelection();
	}

	public object UpdateValueToProperties(List<ObjectPropertyInstance> opis, object value)
	{
		bool flag = false;
		object result = "";
		foreach (ObjectPropertyInstance opi in opis)
		{
			if (opi.Base == null)
			{
				continue;
			}
			EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(opi.ObjectOwner);
			object value2 = opi.Value;
			opi.Value = value;
			object value3 = opi.Value;
			result = opi.Value;
			if (!value3.Equals(value2))
			{
				if (!flag && opis.Count > 1)
				{
					flag = true;
					EditHistoryGroupBegin();
				}
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(opi.ObjectOwner);
				EditHistoryItemObject editHistoryItem = new EditHistoryItemObject(opi.ObjectOwner, EditHistoryObjectAction.PropertyValueChange, dataBefore, dataAfter);
				EditHistoryAddEntry(editHistoryItem);
			}
		}
		if (flag)
		{
			EditHistoryGroupEnd();
		}
		return result;
	}

	public void EditMouseLeftClick(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		EditSetPhysicsOff();
		EditMouseOffset = Microsoft.Xna.Framework.Vector2.Zero;
		m_editLeftMoveAction = EditLeftMoveAction.None;
		if (m_editTargetObjectPropertyInstance != null)
		{
			ObjectData objectAtMousePosition = GetObjectAtMousePosition(autoUnlockLockedLayers: false, SFD.Input.Keyboard.IsAltDown(), prioritizeDynamicObjects: false);
			int num = ((objectAtMousePosition != null && m_editSelectionFitler.CheckTargetIncluded(objectAtMousePosition)) ? objectAtMousePosition.ObjectID : 0);
			switch (m_editTargetObjectPropertyInstance.Base.PropertyClass)
			{
			default:
				throw new NotImplementedException("EditMouseLeftClick does not implement PropertyClass " + m_editTargetObjectPropertyInstance.Base.PropertyClass);
			case ObjectPropertyClass.None:
				throw new NotSupportedException("EditMouseLeftClick does not implement PropertyClass None");
			case ObjectPropertyClass.TargetObjectData:
				UpdateValueToProperties(m_editTargetObjectPropertyInstances, num);
				break;
			case ObjectPropertyClass.TargetObjectDataMultiple:
				if (num != 0)
				{
					int[] collection = Converter.StringToIntArray((string)m_editTargetObjectPropertyInstance.Value);
					List<int> list = new List<int>();
					list.AddRange(collection);
					collection = null;
					if (list.Contains(num))
					{
						list.Remove(num);
					}
					else
					{
						list.Add(num);
					}
					UpdateValueToProperties(m_editTargetObjectPropertyInstances, Converter.IntArrayToString(list.ToArray()));
				}
				break;
			}
			switch (m_editTargetObjectPropertyInstance.Base.PropertyClass)
			{
			default:
				throw new NotImplementedException("EditMouseLeftClick does not implement PropertyClass " + m_editTargetObjectPropertyInstance.Base.PropertyClass);
			case ObjectPropertyClass.None:
				throw new NotSupportedException("EditMouseLeftClick does not implement PropertyClass None");
			case ObjectPropertyClass.TargetObjectData:
				EditCancelTargetObjectPropertyInstance();
				break;
			case ObjectPropertyClass.TargetObjectDataMultiple:
				break;
			}
			EditSetMapEditorPropertiesWindow();
			return;
		}
		if (!IsControlPressed())
		{
			EditSelectedObjects.Clear();
		}
		ObjectData objectData = null;
		int num2 = RenderCategories.Length - 1;
		while (true)
		{
			if (num2 >= 0)
			{
				for (int num3 = RenderCategories[num2].TotalLayers - 1; num3 >= 0; num3--)
				{
					Layer<ObjectData> layer = RenderCategories[num2].GetLayer(num3);
					if (layer.IsVisible && !layer.IsLocked)
					{
						int num4 = layer.Items.Count - 1;
						while (num4 >= 0)
						{
							ObjectData objectData2 = layer.Items[num4];
							if (!EditCheckTouch(objectData2, worldPosition))
							{
								num4--;
								continue;
							}
							objectData = objectData2;
							num3 = -1;
							break;
						}
					}
				}
				if (objectData != null)
				{
					break;
				}
				num2--;
				continue;
			}
			EditAfterSelection();
			return;
		}
		if (!EditSelectedObjects.Contains(objectData))
		{
			EditSelectedObjects.Insert(0, objectData);
			EditSelectedObjectsHandlePostAdd(objectData, EditSelectedObjectsOptions.GroupCheck);
		}
		else
		{
			EditSelectedObjects.Remove(objectData);
			EditSelectedObjectsHandlePostRemove(objectData, EditSelectedObjectsOptions.GroupCheck);
		}
		EditAfterSelection();
	}

	public List<ObjectData> EditSelectedObjectsChangeInWindow(string mapObjectId, EditChangeSelectionInWindowType changeType)
	{
		List<ObjectData> result = new List<ObjectData>();
		AABB aabb = default(AABB);
		Camera.GetAABB(ref aabb);
		World[] array = new World[2] { b2_world_active, b2_world_background };
		for (int i = 0; i < array.Length; i++)
		{
			array[i].QueryAABB(delegate(Fixture fixture)
			{
				if (fixture != null && fixture.GetUserData() != null)
				{
					ObjectData objectData = ObjectData.Read(fixture);
					if (EditGroupID > 0 && objectData.GroupID != EditGroupID)
					{
						return true;
					}
					if (objectData.MapObjectID == mapObjectId && objectData.EditLayerSelectable())
					{
						if (changeType == EditChangeSelectionInWindowType.Add)
						{
							if (!EditSelectedObjects.Contains(objectData))
							{
								EditSelectedObjects.Add(objectData);
								result.Add(objectData);
							}
						}
						else if (changeType == EditChangeSelectionInWindowType.Remove && EditSelectedObjects.Contains(objectData))
						{
							EditSelectedObjects.Remove(objectData);
							result.Add(objectData);
						}
					}
				}
				return true;
			}, ref aabb);
		}
		return result;
	}

	public void EditMouseRightDown(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
	}

	public void EditMouseRightMoveStart(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		m_editRotationMouseStartPosition = worldPosition;
		EditPrepareRotateSelectedObjects();
	}

	public void EditMouseRightMove(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (m_editRotationInAction && EditSelectedObjects.Count > 0)
		{
			EditSetPhysicsOff();
			float num = (m_editRotationMouseStartPosition - worldPosition).X * 0.02f;
			float num2 = (float)Math.PI * 2f;
			while (num > num2)
			{
				num -= num2;
			}
			for (; num < 0f; num += num2)
			{
			}
			EditRotateSelectedObjects(num, updateHistory: false);
		}
	}

	public void EditMouseRightMoveEnd(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		if (m_editRotationInAction)
		{
			EditEndRotateSelectedObjects();
		}
	}

	public void EditMouseRightClick(Microsoft.Xna.Framework.Vector2 worldPosition)
	{
		m_editRotationInAction = false;
	}

	public void EditRotateCW90SelectedObject()
	{
		float rotationAmount = -(float)Math.PI / 2f;
		EditPrepareRotateSelectedObjects();
		EditRotateSelectedObjects(rotationAmount, updateHistory: true);
		m_editRotationInAction = false;
	}

	public void EditDeselectNonRotatingTiles()
	{
		List<ObjectData> list = new List<ObjectData>();
		for (int num = EditSelectedObjects.Count - 1; num >= 0; num--)
		{
			if (!EditSelectedObjects[num].Tile.FreeRotation)
			{
				list.Add(EditSelectedObjects[num]);
				EditSelectedObjects.RemoveAt(num);
			}
		}
		if (list.Count > 0)
		{
			EditSelectedObjectsHandlePostRemove(list, EditSelectedObjectsOptions.GroupCheck);
		}
	}

	public void EditRotateCCW90SelectedObject()
	{
		float rotationAmount = (float)Math.PI / 2f;
		EditPrepareRotateSelectedObjects();
		EditRotateSelectedObjects(rotationAmount, updateHistory: true);
		m_editRotationInAction = false;
	}

	public void EditRotateCW180SelectedObject()
	{
		float rotationAmount = -(float)Math.PI;
		EditPrepareRotateSelectedObjects();
		EditRotateSelectedObjects(rotationAmount, updateHistory: true);
		m_editRotationInAction = false;
	}

	public void EditPrepareRotateSelectedObjects()
	{
		if (m_editLeftMoveAction != EditLeftMoveAction.None)
		{
			return;
		}
		EditDeselectNonRotatingTiles();
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		EditSetPhysicsOff();
		m_editRotationInAction = true;
		m_editRotationStatusesBeforeRotate.Clear();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			m_editRotationStatusesBeforeRotate.Add(new EditHistoryItemObjectData(editSelectedObject));
		}
		m_editRotationPositionCenter = EditGetCenterOfSelection();
	}

	public void EditRotateSelectedObjects(float rotationAmount, bool updateHistory)
	{
		if (rotationAmount < 0f)
		{
			rotationAmount += (float)Math.PI * 2f;
		}
		if (m_editLeftMoveAction != EditLeftMoveAction.None || EditSelectedObjects.Count <= 0)
		{
			return;
		}
		if (updateHistory)
		{
			EditHistoryGroupBegin();
		}
		for (int i = 0; i < EditSelectedObjects.Count; i++)
		{
			EditHistoryItemObjectData editHistoryItemObjectData = m_editRotationStatusesBeforeRotate[i];
			Microsoft.Xna.Framework.Vector2 position = editHistoryItemObjectData.Position;
			float angle = editHistoryItemObjectData.Angle;
			float num = angle + rotationAmount;
			if (i == 0 && DoSnapToGrid())
			{
				float num2 = EditSnapRotation(num, 12f);
				rotationAmount += num2 - num;
				num = angle + rotationAmount;
			}
			Microsoft.Xna.Framework.Vector2 position2 = position - m_editRotationPositionCenter;
			SFDMath.RotatePosition(ref position2, rotationAmount, out position2);
			Microsoft.Xna.Framework.Vector2 position3 = m_editRotationPositionCenter + position2;
			EditSelectedObjects[i].Body.SetTransform(position3, num);
			if (updateHistory)
			{
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(EditSelectedObjects[i]);
				EditHistoryAddEntry(new EditHistoryItemObject(EditSelectedObjects[i], EditHistoryObjectAction.Rotation, editHistoryItemObjectData, dataAfter));
			}
		}
		if (updateHistory)
		{
			EditHistoryGroupEnd();
		}
	}

	public void EditEndRotateSelectedObjects()
	{
		if (m_editLeftMoveAction != EditLeftMoveAction.None || !m_editRotationInAction)
		{
			return;
		}
		m_editRotationInAction = false;
		if (EditSelectedObjects.Count > 0)
		{
			ObjectData od = EditSelectedObjects[0];
			EditHistoryItemObjectData editHistoryItemObjectData = m_editRotationStatusesBeforeRotate[0];
			EditHistoryItemObjectData editHistoryItemObjectData2 = new EditHistoryItemObjectData(od);
			if (Math.Abs((double)(editHistoryItemObjectData.Angle - editHistoryItemObjectData2.Angle) % (Math.PI * 2.0)) <= 9.999999747378752E-05)
			{
				return;
			}
		}
		EditHistoryGroupBegin();
		for (int i = 0; i < EditSelectedObjects.Count; i++)
		{
			ObjectData od2 = EditSelectedObjects[i];
			EditHistoryItemObjectData dataBefore = m_editRotationStatusesBeforeRotate[i];
			EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(od2);
			EditHistoryAddEntry(new EditHistoryItemObject(od2, EditHistoryObjectAction.Rotation, dataBefore, dataAfter));
		}
		EditHistoryGroupEnd();
	}

	public float EditSnapRotation(float rotationToSnap, float toleranceDegree)
	{
		float num = (float)Math.PI / 180f;
		float num2 = rotationToSnap % ((float)Math.PI * 2f);
		float num3 = toleranceDegree * num;
		int num4 = 0;
		float num5;
		while (true)
		{
			if (num4 <= 8)
			{
				num5 = (float)Math.PI * 2f * ((float)num4 / 8f);
				float num6 = num5 - num3;
				float num7 = num5 + num3;
				if (num6 <= num2 && !(num2 > num7))
				{
					break;
				}
				num4++;
				continue;
			}
			return rotationToSnap;
		}
		return num5;
	}

	public bool EditCheckGroupMarkerInSelection()
	{
		return EditSelectedObjects.Any((ObjectData od) => od is ObjectGroupMarker);
	}

	public bool EditCheckUngroupedObjectsInSelection()
	{
		return EditSelectedObjects.Any((ObjectData od) => od.GroupID == 0);
	}

	public List<ObjectData> EditGetGroupMarkersInSelection()
	{
		return EditSelectedObjects.Where((ObjectData od) => od is ObjectGroupMarker).ToList();
	}

	public bool EditRemoveGroupMarkerFromCopyObjects()
	{
		bool result = false;
		for (int num = EditCopyObjects.Count - 1; num >= 0; num--)
		{
			if ((string)EditCopyObjects[num][0] == "GROUPMARKER")
			{
				EditCopyObjects.RemoveAt(num);
				result = true;
			}
		}
		return result;
	}

	public bool EditRemoveGroupMarkerFromSelection()
	{
		bool result = false;
		for (int num = EditSelectedObjects.Count - 1; num >= 0; num--)
		{
			if (EditSelectedObjects[num] is ObjectGroupMarker)
			{
				EditSelectedObjects.RemoveAt(num);
				result = true;
			}
		}
		return result;
	}

	public ushort[] EditGetSelectedGroupIDs()
	{
		return (from x in (from x in EditSelectedObjects
				select x.GroupID into x
				where x != 0
				select x).Distinct()
			orderby x
			select x).ToArray();
	}

	public bool EditCheckCanGroupSelectedObjectsIntoNewGroup()
	{
		if (EditSelectedObjects.Count == 0)
		{
			return false;
		}
		return !EditSelectedObjects.Any((ObjectData od) => od.GroupID > 0);
	}

	public bool EditCheckCanUngroupSelectedObjects()
	{
		if (EditSelectedObjects.Count == 0)
		{
			return false;
		}
		return EditSelectedObjects.Any((ObjectData od) => od.GroupID > 0);
	}

	public bool EditCheckHasMarkedCameraAreaTrigger()
	{
		if (EditSelectedObjects.Count != 1)
		{
			return false;
		}
		return EditSelectedObjects.Any((ObjectData od) => od is ObjectCameraAreaTrigger);
	}

	public void EditSetAsWorldCameraArea()
	{
		ObjectData objectData = EditSelectedObjects.FirstOrDefault((ObjectData od) => od is ObjectCameraAreaTrigger);
		if (objectData != null && objectData is ObjectCameraAreaTrigger)
		{
			CameraAreaPackage cameraAreaPackage = ((ObjectCameraAreaTrigger)objectData).GetCameraAreaPackage();
			string text = string.Format("{0},{1},{2},{3}", new object[4]
			{
				(int)Math.Round(cameraAreaPackage.CameraSafeArea.Top),
				(int)Math.Round(cameraAreaPackage.CameraSafeArea.Left),
				(int)Math.Round(cameraAreaPackage.CameraSafeArea.Bottom),
				(int)Math.Round(cameraAreaPackage.CameraSafeArea.Right)
			});
			if ((string)PropertiesWorld.Get(ObjectPropertyID.World_CameraArea).Value != text)
			{
				EditHistoryItemObjectData dataBefore = new EditHistoryItemObjectData(PropertiesWorld.ObjectOwner);
				PropertiesWorld.Get(ObjectPropertyID.World_CameraArea).Value = text;
				EditHistoryItemObjectData dataAfter = new EditHistoryItemObjectData(PropertiesWorld.ObjectOwner);
				EditHistoryAddEntry(new EditHistoryItemObject(PropertiesWorld.ObjectOwner, EditHistoryObjectAction.PropertyValueChange, dataBefore, dataAfter));
			}
		}
	}

	public ContextMenuStrip EditCreateContextMenu()
	{
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		ToolStripMenuItem toolStripMenuItem = null;
		ToolStripMenuItem[] array = null;
		if (m_editTargetObjectPropertyInstance != null)
		{
			EditCancelTargetObjectPropertyInstance();
			return null;
		}
		if (!EditReadOnly && !IsExtensionScript)
		{
			if (EditGroupID > 0)
			{
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.closegroupedit"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "CLOSEGROUPEDIT";
				toolStripMenuItem.Enabled = true;
				contextMenuStrip.Items.Add(toolStripMenuItem);
				if (EditSelectedObjects.Count > 0)
				{
					toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.ungroup"), null, editContextMenu_Click);
					toolStripMenuItem.Tag = "UNGROUP";
					toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.G | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Control;
					toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
					contextMenuStrip.Items.Add(toolStripMenuItem);
				}
			}
			else if (EditCheckCanGroupSelectedObjectsIntoNewGroup())
			{
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.group"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "GROUP";
				toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.G | System.Windows.Forms.Keys.Control;
				toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
				contextMenuStrip.Items.Add(toolStripMenuItem);
			}
			else if (EditCheckCanUngroupSelectedObjects())
			{
				ushort[] array2 = EditGetSelectedGroupIDs();
				if (array2.Length >= 2)
				{
					ushort[] array3 = array2;
					for (int i = 0; i < array3.Length; i++)
					{
						ushort num = array3[i];
						toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.mergeintogroup", num.ToString()), null, editContextMenu_Click);
						toolStripMenuItem.Tag = "MERGEGROUP_" + num;
						toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.None;
						toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
						contextMenuStrip.Items.Add(toolStripMenuItem);
					}
					contextMenuStrip.Items.Add(new ToolStripMenuItem("-"));
				}
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.entergroupedit"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "ENTERGROUPEDIT";
				toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
				contextMenuStrip.Items.Add(toolStripMenuItem);
				if (array2.Length == 1 && EditCheckUngroupedObjectsInSelection())
				{
					toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.group", array2[0].ToString()), null, editContextMenu_Click);
					toolStripMenuItem.Tag = "GROUP";
					toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.G | System.Windows.Forms.Keys.Control;
					toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
					contextMenuStrip.Items.Add(toolStripMenuItem);
				}
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.ungroup"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "UNGROUP";
				toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.G | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Control;
				toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
				contextMenuStrip.Items.Add(toolStripMenuItem);
			}
			else
			{
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.group"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "GROUP";
				toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.G | System.Windows.Forms.Keys.Control;
				toolStripMenuItem.Enabled = false;
				contextMenuStrip.Items.Add(toolStripMenuItem);
			}
			toolStripMenuItem = new ToolStripMenuItem("-");
			contextMenuStrip.Items.Add(toolStripMenuItem);
			if (EditCheckHasMarkedCameraAreaTrigger())
			{
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.copyvaluestoworldcameraarea"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "WORLDCAMAREA";
				toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.None;
				toolStripMenuItem.Enabled = true;
				contextMenuStrip.Items.Add(toolStripMenuItem);
				toolStripMenuItem = new ToolStripMenuItem("-");
				contextMenuStrip.Items.Add(toolStripMenuItem);
			}
			array = new ToolStripMenuItem[6]
			{
				new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.rotate90CW"), null, editContextMenu_Click),
				null,
				null,
				null,
				null,
				null
			};
			array[0].Tag = "ROTATE90CW";
			array[1] = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.rotate90CCW"), null, editContextMenu_Click);
			array[1].Tag = "ROTATE90CCW";
			array[2] = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.rotate180"), null, editContextMenu_Click);
			array[2].Tag = "ROTATE180CW";
			array[3] = new ToolStripMenuItem("-");
			array[4] = new ToolStripMenuItem(string.Format("{0} ({1})", LanguageHelper.GetText("mapEditor.contextmenu.flipvertically"), LanguageHelper.GetText("mapEditor.contextmenu.shortcut.shift+v")), null, editContextMenu_Click);
			array[4].Tag = "FLIPV";
			array[5] = new ToolStripMenuItem(string.Format("{0} ({1})", LanguageHelper.GetText("mapEditor.contextmenu.fliphorizontally"), LanguageHelper.GetText("mapEditor.contextmenu.shortcut.shift+h")), null, editContextMenu_Click);
			array[5].Tag = "FLIPH";
			string text = LanguageHelper.GetText("mapEditor.contextmenu.rotation");
			ToolStripItem[] dropDownItems = array;
			toolStripMenuItem = new ToolStripMenuItem(text, null, dropDownItems);
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem("-");
			contextMenuStrip.Items.Add(toolStripMenuItem);
			List<ToolStripMenuItem> list = new List<ToolStripMenuItem>();
			HashSet<int> hashSet = new HashSet<int>();
			foreach (int item in from x in EditSelectedObjects.Select((ObjectData x) => x.LocalDrawCategory).Distinct()
				orderby x descending
				select x)
			{
				if (item >= 0 && hashSet.Add(item))
				{
					array = new ToolStripMenuItem[RenderCategories[item].TotalLayers + 2];
					int num2 = 0;
					array[0] = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.tonewlayer"), null, editContextMenu_Click);
					array[0].Tag = "CREATEANDMOVETOLAYER_" + item;
					num2 = 1;
					array[1] = new ToolStripMenuItem("-");
					num2 = 2;
					for (int num3 = RenderCategories[item].TotalLayers - 1; num3 >= 0; num3--)
					{
						string text2 = LanguageHelper.GetText("mapEditor.contextmenu.tolayer", RenderCategories[item].GetLayer(num3).Name);
						array[num2] = new ToolStripMenuItem(text2, null, editContextMenu_Click);
						array[num2].Tag = "MOVETOLAYER_" + item + "_" + num3;
						num2++;
					}
					string text3 = Category.ToName(item);
					dropDownItems = array;
					list.Add(new ToolStripMenuItem(text3, null, dropDownItems));
				}
			}
			array = new ToolStripMenuItem[4 + ((list.Count > 0) ? (list.Count + 1) : 0)];
			array[2] = new ToolStripMenuItem(string.Format("{0} ({1})", LanguageHelper.GetText("mapEditor.contextmenu.tofront"), LanguageHelper.GetText("mapEditor.contextmenu.shortcut.ctrl+shift+up")), null, editContextMenu_Click);
			array[2].Tag = "ZTOP";
			array[0] = new ToolStripMenuItem(string.Format("{0} ({1})", LanguageHelper.GetText("mapEditor.contextmenu.forward"), LanguageHelper.GetText("mapEditor.contextmenu.shortcut.ctrl+up")), null, editContextMenu_Click);
			array[0].Tag = "ZUP";
			array[1] = new ToolStripMenuItem(string.Format("{0} ({1})", LanguageHelper.GetText("mapEditor.contextmenu.backward"), LanguageHelper.GetText("mapEditor.contextmenu.shortcut.ctrl+down")), null, editContextMenu_Click);
			array[1].Tag = "ZDOWN";
			array[3] = new ToolStripMenuItem(string.Format("{0} ({1})", LanguageHelper.GetText("mapEditor.contextmenu.toback"), LanguageHelper.GetText("mapEditor.contextmenu.shortcut.ctrl+shift+down")), null, editContextMenu_Click);
			array[3].Tag = "ZBOTTOM";
			if (list.Count > 0)
			{
				array[4] = new ToolStripMenuItem("-");
				for (int num4 = 0; num4 < list.Count; num4++)
				{
					array[5 + num4] = list[num4];
				}
			}
			string text4 = LanguageHelper.GetText("mapEditor.contextmenu.arrange");
			dropDownItems = array;
			toolStripMenuItem = new ToolStripMenuItem(text4, null, dropDownItems);
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem("-");
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.duplicate"), null, editContextMenu_Click);
			toolStripMenuItem.Tag = "DUPLICATE";
			toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.D | System.Windows.Forms.Keys.Control;
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.copy"), null, editContextMenu_Click);
			toolStripMenuItem.Tag = "COPY";
			toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.C | System.Windows.Forms.Keys.Control;
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.cut"), null, editContextMenu_Click);
			toolStripMenuItem.Tag = "CUT";
			toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.X | System.Windows.Forms.Keys.Control;
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.paste"), null, editContextMenu_Click);
			toolStripMenuItem.Tag = "PASTE";
			toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.V | System.Windows.Forms.Keys.Control;
			toolStripMenuItem.Enabled = EditCopyObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem("-");
			contextMenuStrip.Items.Add(toolStripMenuItem);
			toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.delete"), null, editContextMenu_Click);
			toolStripMenuItem.Tag = "DELETE";
			toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.Delete;
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
		}
		else
		{
			if (EditGroupID > 0)
			{
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.closegroupedit"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "CLOSEGROUPEDIT";
				toolStripMenuItem.Enabled = true;
				contextMenuStrip.Items.Add(toolStripMenuItem);
				toolStripMenuItem = new ToolStripMenuItem("-");
				contextMenuStrip.Items.Add(toolStripMenuItem);
			}
			else if (!EditCheckCanGroupSelectedObjectsIntoNewGroup() && EditCheckCanUngroupSelectedObjects())
			{
				toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.entergroupedit"), null, editContextMenu_Click);
				toolStripMenuItem.Tag = "ENTERGROUPEDIT";
				toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
				contextMenuStrip.Items.Add(toolStripMenuItem);
				toolStripMenuItem = new ToolStripMenuItem("-");
				contextMenuStrip.Items.Add(toolStripMenuItem);
			}
			toolStripMenuItem = new ToolStripMenuItem(LanguageHelper.GetText("mapEditor.contextmenu.copy"), null, editContextMenu_Click);
			toolStripMenuItem.Tag = "COPY";
			toolStripMenuItem.ShortcutKeys = System.Windows.Forms.Keys.C | System.Windows.Forms.Keys.Control;
			toolStripMenuItem.Enabled = EditSelectedObjects.Count > 0;
			contextMenuStrip.Items.Add(toolStripMenuItem);
		}
		return contextMenuStrip;
	}

	public void editContextMenu_Click(object sender, EventArgs e)
	{
		if (sender == null || !(sender is ToolStripMenuItem))
		{
			return;
		}
		EditorCurors editCursor = EditCursor;
		EditCursor = EditorCurors.Loading;
		string text = (string)((ToolStripMenuItem)sender).Tag;
		switch (text)
		{
		case "ZUP":
			EditShiftOrderSelectedObjects(EditShiftOrder.Up);
			break;
		case "CUT":
			EditCutSelectedObjects();
			break;
		case "ZTOP":
			EditShiftOrderSelectedObjects(EditShiftOrder.Top);
			break;
		case "COPY":
			EditCopySelectedObjects();
			break;
		case "FLIPH":
			EditFlipSelectedObjectsH();
			break;
		case "PASTE":
			EditPasteCopiedObjects();
			break;
		case "FLIPV":
			EditFlipSelectedObjectsV();
			break;
		case "GROUP":
			EditGroupSelectedObjects();
			break;
		case "ZDOWN":
			EditShiftOrderSelectedObjects(EditShiftOrder.Down);
			break;
		case "DELETE":
			EditRemoveSelectedObjects();
			break;
		case "ZBOTTOM":
			EditShiftOrderSelectedObjects(EditShiftOrder.Bottom);
			break;
		case "UNGROUP":
			EditUngroupSelectedObjects();
			break;
		case "DUPLICATE":
			EditDuplicateSelectedObjects();
			break;
		case "ROTATE90CW":
			EditRotateCW90SelectedObject();
			break;
		case "ROTATE90CCW":
			EditRotateCCW90SelectedObject();
			break;
		case "ROTATE180CW":
			EditRotateCW180SelectedObject();
			break;
		case "WORLDCAMAREA":
			EditSetAsWorldCameraArea();
			break;
		case "ENTERGROUPEDIT":
			EditEnterGroupEdit();
			break;
		case "CLOSEGROUPEDIT":
			EditCloseGroupEdit();
			break;
		default:
			if (text.StartsWith("CREATEANDMOVETOLAYER_"))
			{
				int num = int.Parse(text.Split(new char[1] { '_' })[1]);
				string value = LanguageHelper.GetText("mapEditor.newLayer");
				if (SimpleInputBox.Show(LanguageHelper.GetText("mapEditor.newLayer"), LanguageHelper.GetText("mapEditor.contextmenu.tonewlayer.promptname"), ref value) == DialogResult.OK)
				{
					int totalLayers = RenderCategories.Categories[num].TotalLayers;
					EditAddLayer(num, totalLayers, value, addHistory: true);
					m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerAdd, num, totalLayers, RenderCategories[num].GetLayer(totalLayers));
					EditMoveSelectedObjectsInCategoryToLayer(num, totalLayers);
					m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, num, totalLayers);
				}
			}
			else if (text.StartsWith("MOVETOLAYER_"))
			{
				string[] array = text.Split(new char[1] { '_' });
				int num2 = int.Parse(array[1]);
				int num3 = int.Parse(array[2]);
				EditMoveSelectedObjectsInCategoryToLayer(num2, num3);
				m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, num2, num3);
			}
			else if (text.StartsWith("MERGEGROUP_"))
			{
				int num4 = int.Parse(text.Split(new char[1] { '_' })[1]);
				EditMergeSelectedObjectsIntoGroup((ushort)num4);
			}
			else
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Unimplemented context menu: {text}");
			}
			break;
		}
		EditCursor = editCursor;
	}

	public void EditSelectArea(SelectionArea area)
	{
		EditSetPhysicsOff();
		List<ObjectData> objects = new List<ObjectData>();
		AABB aabb = default(AABB);
		aabb.lowerBound = Converter.ConvertWorldToBox2D(area.GetBottomLeft());
		aabb.upperBound = Converter.ConvertWorldToBox2D(area.GetTopRight());
		aabb.Grow(0.01f);
		b2_world_active.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture.GetUserData() != null)
			{
				ObjectData item = ObjectData.Read(fixture);
				if (!objects.Contains(item))
				{
					objects.Add(item);
				}
			}
			return true;
		}, ref aabb);
		b2_world_background.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture.GetUserData() != null)
			{
				ObjectData item = ObjectData.Read(fixture);
				if (!objects.Contains(item))
				{
					objects.Add(item);
				}
			}
			return true;
		}, ref aabb);
		for (int num = objects.Count - 1; num >= 0; num--)
		{
			if (!EditCheckObjectSelectable(objects[num]) || !EditCheckTouch(objects[num], area))
			{
				objects.RemoveAt(num);
			}
		}
		if (IsControlPressed())
		{
			if (objects.Count <= 0)
			{
				return;
			}
			foreach (ObjectData item2 in objects)
			{
				EditSelectedObjects.Remove(item2);
			}
			EditSelectedObjects.AddRange(objects);
			EditSelectedObjectsHandlePostAdd(objects, EditSelectedObjectsOptions.GroupCheck);
			EditAfterSelection();
		}
		else
		{
			EditSelectedObjects.Clear();
			EditSelectedObjects.AddRange(objects);
			EditSelectedObjectsHandlePostAdd(objects, EditSelectedObjectsOptions.GroupCheck);
			EditAfterSelection();
		}
	}

	public void EditAfterSelection()
	{
		EditSetMapEditorPropertiesWindow();
		EditUpdateSelectedLayer();
		EditAfterSelectionHighlightReferences();
	}

	public void EditAfterSelectionHighlightReferences()
	{
		EditHighlightObjectsFixed.Clear();
		if (!SFD.Input.Keyboard.IsCapsLockActive())
		{
			return;
		}
		HashSet<ObjectData> hashSet = new HashSet<ObjectData>(EditSelectedObjects);
		if (hashSet.Count <= 0)
		{
			return;
		}
		foreach (ObjectData item in AllObjectData())
		{
			foreach (ObjectPropertyInstance item2 in item.Properties.Items)
			{
				List<ObjectData> referencedObjects = item2.GetReferencedObjects(this);
				if (referencedObjects == null)
				{
					continue;
				}
				foreach (ObjectData item3 in referencedObjects)
				{
					if (hashSet.Contains(item3) && !hashSet.Contains(item))
					{
						EditHighlightObjectsFixed.Add(new Tuple<ObjectData, ObjectData>(item3, item));
					}
				}
			}
		}
	}

	public void EditUpdateSelectedLayer()
	{
		ObjectData objectData = null;
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		objectData = EditSelectedObjects[0];
		string text = Category.ToName(objectData.Tile.DrawCategory);
		int localRenderLayer = objectData.LocalRenderLayer;
		new HashSet<string>();
		bool flag = true;
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			if (editSelectedObject.Tile.DrawCategory != objectData.Tile.DrawCategory || editSelectedObject.LocalRenderLayer != objectData.LocalRenderLayer)
			{
				flag = false;
				break;
			}
		}
		if (flag)
		{
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, text, localRenderLayer);
		}
		else
		{
			m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.LayerSelect, "", -1);
		}
	}

	public void EditSetMapEditorPropertiesWindow()
	{
		List<ObjectProperties> list = new List<ObjectProperties>();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			list.Add(editSelectedObject.Properties);
		}
		if (list.Count == 0)
		{
			list.Add(PropertiesWorld);
		}
		m_game.MapEditorCommand(MapEditorCommands.GUICommand, MapEditorGUICommands.ShowProperties, list, EditSelectedObjects);
	}

	public void SetLayerPropertiesData(int categoryIndex, int layerIndex)
	{
		if (categoryIndex != -1)
		{
			Layer<ObjectData> layer = RenderCategories[categoryIndex].GetLayer(layerIndex);
			if (layer.Tag == null)
			{
				SFDLayerTag sFDLayerTag = null;
				sFDLayerTag = (SFDLayerTag)(layer.Tag = ((categoryIndex != 0) ? new SFDLayerTag() : new SFDLayerFarBGTag()));
				ObjectDataStartParams objectDataStartParams = new ObjectDataStartParams(0, 0, 0, "WORLDLAYER", GameOwner);
				objectDataStartParams.args = new object[2] { layer, categoryIndex };
				sFDLayerTag.Object = ObjectData.CreateNew(objectDataStartParams);
				sFDLayerTag.Object.SetGameWorld(this);
				ObjectData.CreateNewCompleted(sFDLayerTag.Object);
			}
		}
	}

	public void ProcessPlayerAIPackages()
	{
		PlayerAIPackage playerAIPackage = null;
		if (m_botAIPackageForceReProcess != null)
		{
			playerAIPackage = m_botAIPackageForceReProcess;
			m_botAIPackageForceReProcess = null;
		}
		else if (PlayerAIPackages.Count > 0)
		{
			playerAIPackage = PlayerAIPackages.Dequeue();
		}
		if (playerAIPackage == null)
		{
			return;
		}
		if (playerAIPackage.OwnerObject != null && ((!playerAIPackage.OwnerObject.IsPlayer && !playerAIPackage.OwnerObject.IsDisposed && !playerAIPackage.OwnerObject.TerminationInitiated) || (playerAIPackage.OwnerObject.IsPlayer && !playerAIPackage.Owner.IsDisposed && !playerAIPackage.Owner.IsDead && !playerAIPackage.Owner.IsRemoved)))
		{
			playerAIPackage.Process();
			if (playerAIPackage.ForceAnotherProcessPass)
			{
				m_botAIPackageForceReProcess = playerAIPackage;
				playerAIPackage.ForceAnotherProcessPass = false;
			}
		}
		playerAIPackage.ProcessedCount++;
		playerAIPackage.LastProcessTimestamp = ElapsedTotalRealTime;
		playerAIPackage.IsQueued = false;
	}

	public void RecalculateWorldCameraAreas()
	{
		WorldCameraSafeArea = CalculateCameraAreaMinZone(WorldCameraSourceArea);
		WorldCameraMaxArea = CalculateCameraAreaExtraVisibleZone(WorldCameraSafeArea);
	}

	public void UpdateWorldSourceCameraArea(float t, float l, float b, float r)
	{
		WorldCameraSourceArea.Top = t;
		WorldCameraSourceArea.Left = l;
		WorldCameraSourceArea.Bottom = b;
		WorldCameraSourceArea.Right = r;
		RecalculateWorldCameraAreas();
	}

	public void SetActiveCameraAreaID(int id)
	{
		if (ActiveCameraAreaID != id)
		{
			m_currentSafeAreaDelayed = null;
			ActiveCameraAreaID = id;
			m_activeObjectCameraAreaTrigger.ObjectID = id;
			UpdateCameraAreaInfo();
			ObjectCameraAreaTrigger item = m_activeObjectCameraAreaTrigger.Item;
			if (item == null || item.GetCameraSnapping())
			{
				m_primaryTargetArea = null;
				m_dynamicArea = null;
				m_staticArea = null;
				m_individualArea = null;
				m_currentCameraFocusArea = null;
				m_currentSafeAreaDelayed = null;
				m_currentCameraDelayedArea = null;
				m_currentCameraDelayedKeepPlayerInsideArea = null;
				m_dynamicCameraHelper.Reset();
			}
		}
	}

	public CameraAreaPackage GetCameraAreaPackage()
	{
		ObjectCameraAreaTrigger item = m_activeObjectCameraAreaTrigger.Item;
		CameraAreaPackage cameraAreaPackage = ((item != null) ? item.GetCameraAreaPackage() : GetWorldCameraAreaPackage());
		if (cameraAreaPackage.PrimaryTarget == null || cameraAreaPackage.PrimaryTargetIsLocalPlayer)
		{
			Player primaryLocalPlayer = PrimaryLocalPlayer;
			if (primaryLocalPlayer != null && !primaryLocalPlayer.IsDisposed)
			{
				cameraAreaPackage.PrimaryTarget = primaryLocalPlayer.ObjectData;
				cameraAreaPackage.PrimaryTargetIsLocalPlayer = true;
			}
			else
			{
				for (int i = 0; i < LocalPlayers.Length; i++)
				{
					if (LocalPlayers[i] != null && !LocalPlayers[i].IsDisposed && !LocalPlayers[i].IsDead)
					{
						cameraAreaPackage.PrimaryTarget = LocalPlayers[i].ObjectData;
						cameraAreaPackage.PrimaryTargetIsLocalPlayer = true;
						break;
					}
				}
			}
		}
		if (cameraAreaPackage.PrimaryTarget != null)
		{
			if (cameraAreaPackage.PrimaryTarget.IsDisposed)
			{
				cameraAreaPackage.PrimaryTarget = null;
				cameraAreaPackage.PrimaryTargetIsLocalPlayer = false;
			}
			else if (cameraAreaPackage.PrimaryTarget.IsPlayer)
			{
				Player player = (Player)cameraAreaPackage.PrimaryTarget.InternalData;
				if (player != null && player.IsDead && player.DeadTime > CameraAreaCalculateHelper.PLAYER_DEAD_FOCUS_TIME)
				{
					cameraAreaPackage.PrimaryTarget = null;
					cameraAreaPackage.PrimaryTargetIsLocalPlayer = false;
				}
			}
		}
		if (cameraAreaPackage.SecondaryTargets == null || cameraAreaPackage.SecondaryTargets.Count == 0)
		{
			cameraAreaPackage.SecondaryTargets = m_activeSecondaryCameraTargetsAll;
		}
		if (cameraAreaPackage.InfoTargets == null || cameraAreaPackage.InfoTargets.Count == 0)
		{
			cameraAreaPackage.InfoTargets = InfoObjects;
		}
		cameraAreaPackage.FocusPoints = CameraFocusPoints;
		return cameraAreaPackage;
	}

	public bool GetWorldShowDistanceMarkers()
	{
		return (bool)PropertiesWorld.Get(ObjectPropertyID.World_ShowDistanceMarkers).Value;
	}

	public void SetWorldShowDistanceMarkers(bool value)
	{
		PropertiesWorld.Get(ObjectPropertyID.World_ShowDistanceMarkers).Value = value;
	}

	public CameraAreaPackage GetWorldCameraAreaPackage()
	{
		if (m_worldCameraPackage.CameraSafeArea == null)
		{
			m_worldCameraPackage.CameraSafeArea = new Area();
		}
		if (m_worldCameraPackage.CameraMaxArea == null)
		{
			m_worldCameraPackage.CameraMaxArea = new Area();
		}
		m_worldCameraPackage.CameraSafeArea.SetArea(WorldCameraSafeArea);
		m_worldCameraPackage.CameraMaxArea.SetArea(WorldCameraMaxArea);
		return m_worldCameraPackage;
	}

	public void UpdateCameraAreaInfo()
	{
		ObjectCameraAreaTrigger item = m_activeObjectCameraAreaTrigger.Item;
		m_activeCameraSafeArea = item?.GetCameraSafeArea();
		m_activeCameraMaxArea = item?.GetCameraMaxArea();
	}

	public void AddCameraFocusPoint(CameraFocusPoint focusPoint)
	{
		CameraFocusPoints.Add(focusPoint);
	}

	public void CalculateCameraArea(float ms = 0f, bool mainMenuHack = false)
	{
		if (ms > 0f)
		{
			for (int num = CameraFocusPoints.Count - 1; num >= 0; num--)
			{
				CameraFocusPoint cameraFocusPoint = CameraFocusPoints[num];
				cameraFocusPoint.Time -= ms;
				if (cameraFocusPoint.Time <= 0f)
				{
					CameraFocusPoints.RemoveAt(num);
				}
			}
		}
		m_activeSecondaryCameraTargetsAll.Clear();
		foreach (Player player3 in Players)
		{
			if ((!player3.IsDead || player3.DeadTime < CameraAreaCalculateHelper.PLAYER_DEAD_FOCUS_TIME) && player3.CameraSecondaryFocusMode != CameraFocusMode.Ignore)
			{
				m_activeSecondaryCameraTargetsAll.Add(player3.ObjectData);
			}
		}
		if (Streetsweepers.Count > 0)
		{
			foreach (ObjectStreetsweeper streetsweeper in Streetsweepers)
			{
				if (streetsweeper.GetCameraSecondaryFocusMode() != CameraFocusMode.Ignore)
				{
					m_activeSecondaryCameraTargetsAll.Add(streetsweeper);
				}
			}
		}
		CameraMode cameraMode = CurrentCameraMode;
		CameraAreaPackage cameraAreaPackage = GetCameraAreaPackage();
		if (cameraAreaPackage.CameraAreaMode == SFD.Objects.CameraAreaMode.ForceDynamic)
		{
			cameraMode = CameraMode.Dynamic;
		}
		else if (cameraAreaPackage.CameraAreaMode == SFD.Objects.CameraAreaMode.ForceStatic)
		{
			cameraMode = CameraMode.Static;
		}
		else if (cameraAreaPackage.CameraAreaMode == SFD.Objects.CameraAreaMode.ForceIndividual)
		{
			cameraMode = CameraMode.Individual;
		}
		WorkingCameraMode = cameraMode;
		if (mainMenuHack)
		{
			cameraAreaPackage.CameraSafeArea.Left -= 300f / Camera.Zoom;
			cameraAreaPackage.CameraMaxArea.Left -= 300f / Camera.Zoom;
		}
		if (m_dynamicArea == null)
		{
			m_dynamicArea = new Area();
		}
		if (m_staticArea == null)
		{
			m_staticArea = new Area();
		}
		if (m_individualArea == null)
		{
			m_individualArea = new Area();
		}
		if (m_primaryTargetArea == null)
		{
			m_primaryTargetArea = new Area();
		}
		if (m_currentSafeAreaDelayed == null)
		{
			m_currentSafeAreaDelayed = new ExpanderArea(cameraAreaPackage.CameraSafeArea);
			m_currentSafeAreaDelayed.DelayedShrink = false;
			m_currentSafeAreaDelayed.IncreasedGrowSpeed = true;
		}
		m_currentSafeAreaDelayed.Update(cameraAreaPackage.CameraSafeArea, ms);
		cameraAreaPackage.CameraSafeArea.SetArea(m_currentSafeAreaDelayed);
		if (mainMenuHack)
		{
			Area area = m_dynamicCameraHelper.Calculate(cameraAreaPackage, CameraMode.Dynamic, ms);
			area.Grow(40f, 20f);
			m_dynamicArea.SetArea(area);
		}
		else
		{
			m_dynamicArea.SetArea(m_dynamicCameraHelper.Calculate(cameraAreaPackage, CameraMode.Dynamic, ms));
		}
		m_staticArea.SetArea(m_dynamicArea);
		m_dynamicArea.ExtendToOverlap(cameraAreaPackage.CameraSafeArea, 4f);
		m_staticArea.ExtendToOverlap(cameraAreaPackage.CameraSafeArea, 4f);
		m_dynamicArea.IntersectArea(cameraAreaPackage.CameraSafeArea);
		m_staticArea.IntersectArea(cameraAreaPackage.CameraSafeArea);
		if (m_currentCameraDelayedArea == null)
		{
			m_currentCameraDelayedArea = new ExpanderArea(m_dynamicArea);
			m_currentCameraDelayedArea.DelayedShrink = true;
			m_currentCameraDelayedArea.IncreasedGrowSpeed = false;
		}
		if (m_currentCameraDelayedArea.Intersects(m_dynamicArea))
		{
			m_currentCameraDelayedArea.Update(m_dynamicArea, ms);
			m_dynamicArea.SetArea(m_currentCameraDelayedArea);
		}
		else
		{
			m_currentCameraDelayedArea.SetArea(m_dynamicArea);
		}
		float fAdjustedWidth = m_dynamicArea.Width;
		float fAdjustedHeight = m_dynamicArea.Height;
		GameSFD.AdjustResolutionGrowToAspectRatio(fAdjustedWidth, fAdjustedHeight, GameSFD.GAME_RATIO, out fAdjustedWidth, out fAdjustedHeight);
		GameSFD.AdjustResolutionToMinMaxSize(fAdjustedWidth, fAdjustedHeight, out fAdjustedWidth, out fAdjustedHeight, cameraAreaPackage.CameraMaxArea.Width, cameraAreaPackage.CameraMaxArea.Height);
		m_dynamicArea.SetDimensions(fAdjustedWidth, fAdjustedHeight);
		m_dynamicArea.MoveInside(cameraAreaPackage.CameraSafeArea);
		if (CameraAreaCalculateHelper.PRIMARY_TARGET_KEEP_INSIDE_ALWAYS && cameraAreaPackage.PrimaryTarget != null && !cameraAreaPackage.PrimaryTarget.IsDisposed)
		{
			ObjectData translucenceObject = cameraAreaPackage.PrimaryTarget.GetTranslucenceObject();
			if (translucenceObject != null && !translucenceObject.IsDisposed)
			{
				m_primaryTargetArea.SetArea(translucenceObject.GetWorldCenterPosition(), CameraAreaCalculateHelper.PRIMARY_TARGET_PADDING_ALWAYS_INSIDE_PADDING);
				if (m_currentCameraDelayedKeepPlayerInsideArea == null)
				{
					m_currentCameraDelayedKeepPlayerInsideArea = new ExpanderArea(m_primaryTargetArea);
					m_currentCameraDelayedKeepPlayerInsideArea.DelayedShrink = false;
					m_currentCameraDelayedKeepPlayerInsideArea.IncreasedGrowSpeed = true;
					m_staticSnapPos = true;
				}
				m_currentCameraDelayedKeepPlayerInsideArea.Update(m_primaryTargetArea, ms);
				m_dynamicArea.MoveToIncludeArea(m_currentCameraDelayedKeepPlayerInsideArea, cameraAreaPackage.CameraSafeArea);
			}
		}
		if (cameraMode == CameraMode.Static)
		{
			m_staticArea.SetDimensions(cameraAreaPackage.CameraSafeArea.Width, cameraAreaPackage.CameraSafeArea.Height);
			fAdjustedWidth = m_staticArea.Width;
			fAdjustedHeight = m_staticArea.Height;
			GameSFD.AdjustResolutionGrowToAspectRatio(fAdjustedWidth, fAdjustedHeight, GameSFD.GAME_RATIO, out fAdjustedWidth, out fAdjustedHeight);
			GameSFD.AdjustResolutionToMinMaxSize(fAdjustedWidth, fAdjustedHeight, out fAdjustedWidth, out fAdjustedHeight, cameraAreaPackage.CameraMaxArea.Width, cameraAreaPackage.CameraMaxArea.Height);
			m_staticArea.SetDimensions(fAdjustedWidth, fAdjustedHeight);
			m_staticArea.MoveInside(cameraAreaPackage.CameraSafeArea);
			Microsoft.Xna.Framework.Vector2 center = m_staticArea.Center;
			if (m_staticSnapPos)
			{
				m_staticAreaCenterPos = center;
				m_staticArea.SetCenter(m_staticAreaCenterPos);
				m_staticSnapPos = false;
			}
			else
			{
				Microsoft.Xna.Framework.Vector2 vector = center - m_staticAreaCenterPos;
				float num2 = vector.CalcSafeLength();
				if (num2 > 0.01f)
				{
					Microsoft.Xna.Framework.Vector2 vector2 = Microsoft.Xna.Framework.Vector2.Normalize(vector);
					if (vector2.IsValid())
					{
						float amount = num2 / 5f;
						float num3 = Microsoft.Xna.Framework.MathHelper.Lerp(0.01f, 0.1f, amount) * ms;
						if (num3 > 0.01f)
						{
							m_staticAreaCenterPos += vector2 * Math.Min(num3, num2);
							m_staticArea.SetCenter(m_staticAreaCenterPos);
						}
					}
				}
			}
		}
		else
		{
			m_staticSnapPos = true;
		}
		if (cameraMode == CameraMode.Individual)
		{
			if (SpectatingPlayer != null && (SpectatingPlayer.IsRemoved || SpectatingPlayer.IsDead))
			{
				SpectatingPlayer = null;
			}
			Player player = null;
			float num4 = 0f;
			if (EditTestMode)
			{
				GameUser localGameUser = GameInfo.GetLocalGameUser(GameInfo.PrimaryLocalUserIndex);
				if (localGameUser != null)
				{
					Player playerByUserIdentifier = GetPlayerByUserIdentifier(localGameUser.UserIdentifier);
					if (playerByUserIdentifier != null && SpectatingPlayer != playerByUserIdentifier)
					{
						SpectatingPlayer = playerByUserIdentifier;
					}
				}
			}
			else
			{
				for (int i = 0; i < LocalPlayers.Length; i++)
				{
					Player player2 = LocalPlayers[i];
					if (player2 == null || player2.IsRemoved || player2.IsDead)
					{
						continue;
					}
					if (SpectatingPlayer != null && !SpectatingPlayer.IsBot)
					{
						float num5 = Microsoft.Xna.Framework.Vector2.Distance(player2.Position, (player ?? SpectatingPlayer).Position);
						if (num5 > num4)
						{
							num4 = num5;
							player = player2;
						}
					}
					else
					{
						SpectatingPlayer = player2;
					}
				}
			}
			if (m_validPlayersToSpectate == null)
			{
				m_validPlayersToSpectate = new List<Player>();
			}
			else
			{
				m_validPlayersToSpectate.Clear();
			}
			for (int j = 0; j < Players.Count; j++)
			{
				if (!Players[j].IsDead && Players[j].GetGameUser() != null)
				{
					m_validPlayersToSpectate.Add(Players[j]);
				}
			}
			if (SpectatingPlayer != null && SpectatingPlayer.IsLocal)
			{
				CanManuallyChangeSpectatingPlayer = false;
				m_spectatePlayerCooldown = 3000f;
			}
			else
			{
				GameUser gameUser = LocalGameUsers[0];
				if (!CanManuallyChangeSpectatingPlayer)
				{
					if (gameUser != null && !gameUser.IsDisposed && gameUser.SpectateImmediately)
					{
						CanManuallyChangeSpectatingPlayer = true;
						m_spectatePlayerCooldown = 0f;
					}
					else
					{
						m_spectatePlayerCooldown -= ms;
						if (m_spectatePlayerCooldown <= 0f)
						{
							CanManuallyChangeSpectatingPlayer = true;
							ChangeSpectatingPlayer(1);
							m_spectatePlayerCooldown = 3000f;
						}
					}
				}
				else if (SpectatingPlayer != null)
				{
					m_spectatePlayerCooldown = 3000f;
					gameUser.SpectateImmediately = false;
				}
				else
				{
					m_spectatePlayerCooldown -= ms;
					if (m_spectatePlayerCooldown <= 0f)
					{
						ChangeSpectatingPlayer(1);
					}
				}
			}
			if (SpectatingPlayer != null)
			{
				Microsoft.Xna.Framework.Vector2 pos = new Microsoft.Xna.Framework.Vector2(SpectatingPlayer.Position.X, SpectatingPlayer.Position.Y + 20f);
				if (SpectatingPlayer.CurrentAction == PlayerAction.ManualAim)
				{
					float aimAngle = SpectatingPlayer.AimAngle;
					Microsoft.Xna.Framework.Vector2 vector3 = new Microsoft.Xna.Framework.Vector2((float)SpectatingPlayer.LastDirectionX * (float)Math.Cos(aimAngle), 0f - (float)Math.Sin(aimAngle));
					vector3 *= 50f;
					pos += vector3;
				}
				m_individualArea.SetArea(pos, m_individualZoom);
				if (m_currentCameraDelayedIndividualArea == null)
				{
					m_currentCameraDelayedIndividualArea = new ExpanderArea(m_individualArea);
					m_currentCameraDelayedIndividualArea.DelayedShrink = false;
					m_currentCameraDelayedIndividualArea.IncreasedGrowSpeed = false;
				}
			}
			if (m_currentCameraDelayedIndividualArea != null)
			{
				if (m_currentCameraDelayedIndividualArea.Intersects(m_individualArea))
				{
					m_currentCameraDelayedIndividualArea.Update(m_individualArea, ms);
					m_individualArea.SetArea(m_currentCameraDelayedIndividualArea);
				}
				else
				{
					m_currentCameraDelayedIndividualArea.SetArea(m_individualArea);
				}
			}
			else
			{
				m_individualArea.SetArea(m_dynamicArea);
			}
			m_minIndividualZoom = Math.Min(cameraAreaPackage.CameraSafeArea.Width, cameraAreaPackage.CameraSafeArea.Height) / 1.5f;
			if (player != null)
			{
				if (num4 < 30f)
				{
					num4 = 30f;
				}
				m_maxIndividualZoom = Math.Min(m_minIndividualZoom, num4);
			}
			else if (m_maxIndividualZoom > 30f)
			{
				m_maxIndividualZoom = 30f;
			}
			if (FixedIndividualZoom != -1f)
			{
				m_individualZoom = m_minIndividualZoom + FixedIndividualZoom / 100f * (m_maxIndividualZoom - m_minIndividualZoom);
			}
			else
			{
				float num6 = Microsoft.Xna.Framework.MathHelper.Clamp(m_individualZoom, m_maxIndividualZoom, m_minIndividualZoom);
				if (m_individualZoom < num6 || m_individualZoom > num6)
				{
					m_individualZoom = num6;
				}
			}
			float fAdjustedWidth2 = m_individualArea.Width;
			float fAdjustedHeight2 = m_individualArea.Height;
			GameSFD.AdjustResolutionGrowToAspectRatio(fAdjustedWidth2, fAdjustedHeight2, GameSFD.GAME_RATIO, out fAdjustedWidth2, out fAdjustedHeight2);
			GameSFD.AdjustResolutionToMinMaxSize(fAdjustedWidth2, fAdjustedHeight2, out fAdjustedWidth2, out fAdjustedHeight2, cameraAreaPackage.CameraMaxArea.Width, cameraAreaPackage.CameraMaxArea.Height);
			m_individualArea.SetDimensions(fAdjustedWidth2, fAdjustedHeight2);
			m_individualArea.MoveInside(cameraAreaPackage.CameraSafeArea);
		}
		else
		{
			CanManuallyChangeSpectatingPlayer = true;
			m_spectatePlayerCooldown = 0f;
		}
		Area area2 = cameraMode switch
		{
			CameraMode.Dynamic => m_dynamicArea, 
			CameraMode.Individual => m_individualArea, 
			_ => m_staticArea, 
		};
		if (m_currentCameraFocusArea == null)
		{
			m_currentCameraFocusArea = new Area(area2);
		}
		float value = area2.Left - m_currentCameraFocusArea.Left;
		float value2 = area2.Right - m_currentCameraFocusArea.Right;
		float value3 = area2.Bottom - m_currentCameraFocusArea.Bottom;
		float value4 = area2.Top - m_currentCameraFocusArea.Top;
		float num7 = Math.Max(Math.Max(Math.Abs(value), Math.Abs(value2)), Math.Max(Math.Abs(value3), Math.Abs(value4)));
		if (num7 > 0.01f)
		{
			if (num7 < ExpanderArea.FINAL_TARGET_DEACCELERATION_DISTANCE)
			{
				float num8 = num7 / ExpanderArea.FINAL_TARGET_DEACCELERATION_DISTANCE;
				num8 = ExpanderArea.FINAL_TARGET_DEACCELERATION_MIN_SPEED_FACTOR + num8 * (1f - ExpanderArea.FINAL_TARGET_DEACCELERATION_MIN_SPEED_FACTOR);
				ms *= num8;
			}
			float val = ExpanderArea.FINAL_TARGET_AREA_SPEED * ms;
			num7 = 1f / num7;
			value = (float)Math.Sign(value) * Math.Min(val, Math.Abs(value)) * (Math.Abs(value) * num7);
			value2 = (float)Math.Sign(value2) * Math.Min(val, Math.Abs(value2)) * (Math.Abs(value2) * num7);
			value3 = (float)Math.Sign(value3) * Math.Min(val, Math.Abs(value3)) * (Math.Abs(value3) * num7);
			value4 = (float)Math.Sign(value4) * Math.Min(val, Math.Abs(value4)) * (Math.Abs(value4) * num7);
			m_currentCameraFocusArea.Left += value;
			m_currentCameraFocusArea.Right += value2;
			m_currentCameraFocusArea.Bottom += value3;
			m_currentCameraFocusArea.Top += value4;
		}
		Camera.SetArea(m_currentCameraFocusArea);
		if (mainMenuHack)
		{
			float num9 = 0.5f;
			Camera.ChangePositionX((0f - 300f / Camera.Zoom) * num9);
		}
	}

	public bool ChangeSpectatingPlayer(int changeIndex)
	{
		if (CanManuallyChangeSpectatingPlayer && WorkingCameraMode == CameraMode.Individual)
		{
			if (m_validPlayersToSpectate != null && m_validPlayersToSpectate.Count != 0)
			{
				if (!CheckAllLocalPlayersDead(spectatingLocalUserIsConsideredAlive: false))
				{
					return false;
				}
				int num = -1;
				if (SpectatingPlayer != null)
				{
					for (int i = 0; i < m_validPlayersToSpectate.Count; i++)
					{
						if (m_validPlayersToSpectate[i] == SpectatingPlayer)
						{
							num = i;
							break;
						}
					}
				}
				else
				{
					num = ((m_lastSpectateIndex > 0) ? (m_lastSpectateIndex - 1) : 0);
				}
				num = ((changeIndex < 0) ? ((num <= 0 || num >= m_validPlayersToSpectate.Count) ? (m_validPlayersToSpectate.Count - 1) : (num - 1)) : ((num < m_validPlayersToSpectate.Count - 1) ? (num + 1) : 0));
				SpectatingPlayer = m_validPlayersToSpectate[num];
				m_lastSpectateIndex = num;
				return true;
			}
			return false;
		}
		return false;
	}

	public void UpdateCameraZoom(float ms)
	{
		if (FixedIndividualZoom != -1f)
		{
			return;
		}
		if (SFD.Input.Keyboard.IsKeyDown(Camera.ZoomInCameraKey))
		{
			if (m_individualZoom > m_maxIndividualZoom)
			{
				m_individualZoom -= 0.3f * ms;
				if (m_individualZoom < m_maxIndividualZoom)
				{
					m_individualZoom = m_maxIndividualZoom;
				}
			}
		}
		else if (SFD.Input.Keyboard.IsKeyDown(Camera.ZoomOutCameraKey) && m_individualZoom < m_minIndividualZoom)
		{
			m_individualZoom += 0.3f * ms;
			if (m_individualZoom > m_minIndividualZoom)
			{
				m_individualZoom = m_minIndividualZoom;
			}
		}
	}

	public static Area CalculateCameraAreaMinZone(Area area)
	{
		Area area2 = area.Copy();
		GameSFD.AdjustResolutionShrinkToAspectRatio(area2.Width, area.Height, 1.3333334f, out var fAdjustedWidth, out var fAdjustedHeight);
		GameSFD.AdjustResolutionToMinMaxSize(fAdjustedWidth, fAdjustedHeight, out fAdjustedWidth, out fAdjustedHeight);
		area2.SetDimensions(Math.Max(area2.Width, fAdjustedWidth), Math.Max(area2.Height, fAdjustedHeight));
		return area2;
	}

	public static Area CalculateCameraAreaExtraVisibleZone(Area area)
	{
		Area area2 = CalculateCameraAreaMinZone(area);
		GameSFD.AdjustResolutionShrinkToAspectRatio(area2.Width, area2.Height, 1.8333333f, out var fAdjustedWidth, out var fAdjustedHeight);
		float secondaryHeightConstraint = 0.75f * fAdjustedWidth;
		GameSFD.AdjustResolutionGrowToAspectRatio(area2.Width, area2.Height, 1.3333334f, out var fAdjustedWidth2, out var fAdjustedHeight2);
		GameSFD.AdjustResolutionToMinMaxSize(fAdjustedWidth2, fAdjustedHeight2, out fAdjustedWidth2, out fAdjustedHeight2, 0f, secondaryHeightConstraint);
		GameSFD.AdjustResolutionGrowToAspectRatio(area2.Width, area2.Height, 2.3333333f, out var fAdjustedWidth3, out var fAdjustedHeight3);
		GameSFD.AdjustResolutionToMinMaxSize(fAdjustedWidth3, fAdjustedHeight3, out fAdjustedWidth3, out fAdjustedHeight3, 0f, secondaryHeightConstraint);
		fAdjustedWidth = Math.Max(fAdjustedWidth2, fAdjustedWidth3);
		fAdjustedHeight = Math.Max(fAdjustedHeight2, fAdjustedHeight3);
		area2.SetDimensions(Math.Max(area2.Width, fAdjustedWidth), Math.Max(area2.Height, fAdjustedHeight));
		return area2;
	}

	public void SetCameraMode(CameraMode newCameraMode)
	{
		if (CurrentCameraMode != newCameraMode)
		{
			CurrentCameraMode = newCameraMode;
			CalculateCameraArea();
		}
	}

	public void SetAllowedCameraModes(CameraMode cameraModes)
	{
		m_allowedCameraModes = cameraModes;
		if (GameOwner == GameOwnerEnum.Server)
		{
			NewObjectData newObjectData = new NewObjectData(NewObjectType.SetAllowedCameraModes);
			newObjectData.Write((int)cameraModes);
			NewObjectsCollection.AddNewObject(newObjectData);
		}
		else if ((m_allowedCameraModes & CurrentCameraMode) != CurrentCameraMode)
		{
			ToggleCameraMode();
		}
	}

	public CameraMode GetAllowedCameraModes()
	{
		return m_allowedCameraModes;
	}

	public void SetCameraModeForPlayers(CameraMode mode)
	{
		SetCameraMode(mode);
		if (GameOwner == GameOwnerEnum.Server)
		{
			NewObjectData newObjectData = new NewObjectData(NewObjectType.SetCurrentCameraMode);
			newObjectData.Write((int)mode);
			NewObjectsCollection.AddNewObject(newObjectData);
		}
	}

	public void ToggleCameraMode()
	{
		switch (CurrentCameraMode)
		{
		case CameraMode.Dynamic:
			if ((m_allowedCameraModes & CameraMode.Individual) == CameraMode.Individual)
			{
				SetCameraMode(CameraMode.Individual);
			}
			break;
		case CameraMode.Static:
			if ((m_allowedCameraModes & CameraMode.Dynamic) == CameraMode.Dynamic)
			{
				SetCameraMode(CameraMode.Dynamic);
			}
			break;
		default:
			SetCameraMode(CameraMode.Dynamic);
			break;
		case CameraMode.Individual:
			if ((m_allowedCameraModes & CameraMode.Static) == CameraMode.Static)
			{
				SetCameraMode(CameraMode.Static);
			}
			break;
		}
	}

	public void SetCameraArea(int top, int left, int bottom, int right)
	{
		PropertiesWorld.Get(ObjectPropertyID.World_CameraArea).Value = string.Format("{0},{1},{2},{3}", new object[4] { top, left, bottom, right });
	}

	public bool CheckRescanDrawingZoneRequired()
	{
		Camera.GetAABB(ref m_worldCurrentDrawZone);
		return (m_worldCurrentDrawZone.lowerBound.X > m_worldRedrawZoneInner.lowerBound.X) | (m_worldCurrentDrawZone.lowerBound.X < m_worldRedrawZoneOuter.lowerBound.X) | (m_worldCurrentDrawZone.upperBound.X < m_worldRedrawZoneInner.upperBound.X) | (m_worldCurrentDrawZone.upperBound.X > m_worldRedrawZoneOuter.upperBound.X) | (m_worldCurrentDrawZone.lowerBound.Y > m_worldRedrawZoneInner.lowerBound.Y) | (m_worldCurrentDrawZone.lowerBound.Y < m_worldRedrawZoneOuter.lowerBound.Y) | (m_worldCurrentDrawZone.upperBound.Y < m_worldRedrawZoneInner.upperBound.Y) | (m_worldCurrentDrawZone.upperBound.Y > m_worldRedrawZoneOuter.upperBound.Y);
	}

	public void PerformRescanDrawingZone()
	{
		float num = Converter.ConvertWorldToBox2D(64f);
		m_worldRedrawZoneInner = m_worldCurrentDrawZone;
		m_worldRedrawZoneInner.Grow(0f - num);
		m_worldRedrawZoneOuter = m_worldCurrentDrawZone;
		m_worldRedrawZoneOuter.Grow(num);
		for (int i = 0; i < 30; i++)
		{
			Layers<ObjectData> layers = RenderCategories[i];
			for (int j = 0; j < layers.TotalLayers; j++)
			{
				Layer<ObjectData> layer = layers.GetLayer(j);
				if (i == 0)
				{
					SFDLayerFarBGTag sFDLayerFarBGTag = (SFDLayerFarBGTag)layer.Tag;
					m_worldCurrentFarBGDrawZone = m_worldRedrawZoneOuter;
					Microsoft.Xna.Framework.Vector2 vector = m_worldRedrawZoneOuter.GetCenter() - m_worldRedrawZoneOuter.GetCenter() * sFDLayerFarBGTag.FarBackgroundMovementFactor;
					m_worldCurrentFarBGDrawZone.lowerBound -= vector;
					m_worldCurrentFarBGDrawZone.upperBound -= vector;
				}
				foreach (ObjectData item in layer.Items)
				{
					if (item.Body.GetType() == Box2D.XNA.BodyType.Dynamic)
					{
						item.DrawFlag = true;
						continue;
					}
					if (item.DrawOutsideCameraArea)
					{
						item.DrawFlag = true;
					}
					else if (item.Body.GetType() == Box2D.XNA.BodyType.Static && !item.DrawOutsideCameraArea)
					{
						item.DrawFlag = false;
					}
					if ((i == 0) & !item.DrawFlag)
					{
						AABB aabb = default(AABB);
						item.Body.GetAABB(out aabb);
						if (AABB.TestOverlap(ref m_worldCurrentFarBGDrawZone, ref aabb))
						{
							item.DrawFlag = true;
						}
					}
				}
			}
		}
		ObjectData bd;
		b2_world_active.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture.GetUserData() != null && fixture.GetBody().GetType() == Box2D.XNA.BodyType.Static)
			{
				bd = (ObjectData)fixture.GetUserData();
				bd.DrawFlag |= bd.DoDraw;
			}
			return true;
		}, ref m_worldRedrawZoneOuter);
		if (!EditMode)
		{
			return;
		}
		b2_world_background.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture.GetUserData() != null)
			{
				bd = (ObjectData)fixture.GetUserData();
				bd.DrawFlag |= bd.DoDraw;
			}
			return true;
		}, ref m_worldRedrawZoneOuter);
	}

	public bool CheckBodyInsideRenderingArea(Body body)
	{
		body.GetAABB(out var aabb);
		return AABB.TestOverlap(ref aabb, ref m_worldRedrawZoneOuter);
	}

	public BodyData GetBodyDataFromObjectData(ObjectData objectData)
	{
		return new BodyData(objectData.ObjectID);
	}

	public Player CreatePlayer(Microsoft.Xna.Framework.Vector2 worldPosition, Profile playerProfile, Team playerTeam, Player.PlayerSpawnAnimation spawnAnimation = Player.PlayerSpawnAnimation.None)
	{
		string name = playerProfile.Name;
		string result = playerProfile.Name;
		string errorMsg = "";
		if (!Profile.ValidateName(result, validateRestrictedNames: false, out result, out errorMsg))
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Player name defaulted to " + result);
		}
		playerProfile.Name = result;
		CreatePlayer(new SpawnObjectInformation(IDCounter.NextObjectData("PLAYER"), worldPosition), playerProfile, playerTeam, spawnAnimation);
		playerProfile.Name = name;
		Player player = Players[Players.Count - 1];
		player.LastDirectionX = 1;
		return player;
	}

	public Body CreatePlayer(SpawnObjectInformation spawnObject, Profile playerProfile, Team playerTeam, Player.PlayerSpawnAnimation spawnAnimation = Player.PlayerSpawnAnimation.None)
	{
		NewObjectData newObjectData = null;
		if (GameOwner == GameOwnerEnum.Server)
		{
			newObjectData = new NewObjectData(NewObjectType.CreatePlayer);
			newObjectData.Write(spawnObject);
			newObjectData.Write(playerProfile);
			newObjectData.Write((int)playerTeam);
			newObjectData.Write((ushort)spawnAnimation);
			NewObjectsCollection.AddNewObject(newObjectData);
		}
		ConsoleOutput.ShowMessage(ConsoleOutputType.Information, $"{GameOwner.ToString()}: Creating '{spawnObject.ObjectData.MapObjectID}' with id {spawnObject.ObjectData.ObjectID}");
		spawnObject.ObjectDataUsed = true;
		ObjectData objectData = spawnObject.ObjectData;
		BodyData bodyDataFromObjectData = GetBodyDataFromObjectData(objectData);
		Tile tile = TileDatabase.Get("s_plr");
		World world = b2_world_active;
		Microsoft.Xna.Framework.Vector2 worldPosition = spawnObject.WorldPosition;
		FixtureDef fixtureDef = new FixtureDef();
		fixtureDef.density = 1f;
		fixtureDef.friction = 0f;
		fixtureDef.restitution = 0f;
		fixtureDef.filter = tile.TileFixtures[0].Filter.box2DFilter;
		fixtureDef.filter.blockExplosions = false;
		fixtureDef.filter.projectileHit = true;
		fixtureDef.filter.absorbProjectile = true;
		fixtureDef.filter.objectStrength = 0f;
		BodyDef bodyDef = new BodyDef();
		bodyDef.position = Converter.WorldToBox2D(worldPosition);
		bodyDef.type = Box2D.XNA.BodyType.Dynamic;
		CircleShape circleShape = new CircleShape();
		circleShape._p = Microsoft.Xna.Framework.Vector2.Zero;
		circleShape._radius = 0.16f;
		fixtureDef.shape = circleShape;
		PolygonShape polygonShape = new PolygonShape();
		float num = Converter.WorldToBox2D(6f);
		polygonShape.SetAsBox(Converter.WorldToBox2D(3.5f), num);
		for (int i = 0; i < 4; i++)
		{
			polygonShape._vertices[i] = new Microsoft.Xna.Framework.Vector2(polygonShape._vertices[i].X, polygonShape._vertices[i].Y + num);
		}
		Body body = world.CreateBody(bodyDef);
		body.SetBullet(flag: true);
		body.SetUserData(bodyDataFromObjectData);
		bodyDataFromObjectData.SetOwner(body);
		Fixture fixture = body.CreateFixture(fixtureDef);
		fixture.SetUserData(objectData);
		fixture.ID = "cicle";
		fixture.TileFixtureIndex = 0;
		fixture.SetMass(Converter.ConvertMassKGToBox2D(5f));
		objectData.AddOwner(fixture);
		fixtureDef.shape = polygonShape;
		fixture = body.CreateFixture(fixtureDef);
		fixture.SetUserData(objectData);
		fixture.ID = "rectangle";
		fixture.TileFixtureIndex = 1;
		fixture.SetMass(Converter.ConvertMassKGToBox2D(5f));
		objectData.AddOwner(fixture);
		objectData.SetTile(tile);
		body.ResetMassData();
		body.AllowSleeping(flag: false);
		body.SetFixedRotation(flag: true);
		body.SetBullet(flag: true);
		Player player = new Player(m_game, body, this, GameOwner);
		body.SetTransform(Converter.WorldToBox2D(worldPosition), 0f);
		player.PreBox2DPosition = Converter.WorldToBox2D(worldPosition);
		player.Position = worldPosition;
		player.SpawnAnimation = spawnAnimation;
		player.ApplyProfile(playerProfile?.Copy());
		player.InitCurrentTeam(playerTeam);
		player.SetAnimation(Animation.Idle);
		Players.Add(player);
		PlayersLookup.Add(player.ObjectID, player);
		objectData.SetGameWorld(this);
		objectData.AddToRenderLayer(0);
		objectData.InitializeBase();
		objectData.Initialize();
		objectData.DrawFlag = true;
		ObjectData.CreateNewCompleted(objectData);
		if (newObjectData != null)
		{
			newObjectData.OriginalObjectData = objectData;
		}
		CheckQueuedPlayerUpdates(player);
		body.SetFixedRotation(flag: true);
		body.SetBullet(flag: true);
		m_newlyCreatedPlayers.Add(player);
		return body;
	}

	public void HandleQueuedNewPlayers()
	{
		if (m_newlyCreatedPlayers.Count > 0)
		{
			if (GameOwner != GameOwnerEnum.Client)
			{
				RunScriptOnPlayerCreatedCallbacks(m_newlyCreatedPlayers);
			}
			m_newlyCreatedPlayers.Clear();
		}
	}

	public Body CreateTile(SpawnObjectInformation spawnObject)
	{
		try
		{
			NewObjectData newObjectData = null;
			if (GameOwner == GameOwnerEnum.Server)
			{
				newObjectData = new NewObjectData(NewObjectType.CreateTile);
				newObjectData.Write(spawnObject);
				NewObjectsCollection.AddNewObject(newObjectData);
			}
			Tile tile = spawnObject.ObjectData.Tile;
			World world = b2_world_active;
			if (spawnObject.ObjectData.IsFarBG)
			{
				world = b2_world_background;
			}
			if (m_bd == null)
			{
				m_bd = new BodyDef();
			}
			switch (tile.Type)
			{
			case Tile.TYPE.Dynamic:
				m_bd.type = Box2D.XNA.BodyType.Dynamic;
				break;
			case Tile.TYPE.Static:
				m_bd.type = Box2D.XNA.BodyType.Static;
				break;
			}
			m_bd.position = Converter.WorldToBox2D(spawnObject.WorldPosition);
			m_bd.angle = spawnObject.Rotation;
			m_bd.angularDamping = 0.3f;
			m_bd.linearDamping = 0.01f;
			spawnObject.ObjectDataUsed = true;
			ObjectData objectData = spawnObject.ObjectData;
			BodyData bodyDataFromObjectData = GetBodyDataFromObjectData(objectData);
			Body body = world.CreateBody(m_bd);
			body.SetUserData(bodyDataFromObjectData);
			bodyDataFromObjectData.SetOwner(body);
			for (int i = 0; i < tile.TileFixtures.Count; i++)
			{
				Shape shape;
				if (tile.TileFixtures[i].CirclePoint == null)
				{
					try
					{
						shape = new PolygonShape();
						((PolygonShape)shape).Set(tile.TileFixtures[i].GetIndices(objectData.FaceDirection), tile.TileFixtures[i].Indices.Length);
					}
					catch (Exception ex)
					{
						throw new Exception("Error: Could not create tile " + tile.ToString() + " make sure the vertices is in a CCW order\r\n" + ex.ToString());
					}
				}
				else
				{
					try
					{
						shape = tile.TileFixtures[i].GetCircleShape(objectData.FaceDirection);
					}
					catch (Exception ex2)
					{
						throw new Exception("Error: Could not create tile " + tile.ToString() + " make sure the vertices is in a CCW order\r\n" + ex2.ToString());
					}
				}
				if (m_fd == null)
				{
					m_fd = new FixtureDef();
				}
				Material material = ((tile.TileFixtures[i].Material != null) ? tile.TileFixtures[i].Material : tile.Material);
				m_fd.restitution = material.Restitution;
				m_fd.friction = material.Friction;
				m_fd.density = material.Density;
				m_fd.filter = default(Filter);
				m_fd.filter.categoryBits = tile.TileFixtures[i].Filter.box2DFilter.categoryBits;
				m_fd.filter.maskBits = tile.TileFixtures[i].Filter.box2DFilter.maskBits;
				m_fd.filter.cloudRotation = ((objectData.FaceDirection == 1) ? tile.TileFixtures[i].Filter.box2DFilter.cloudRotation : (0f - tile.TileFixtures[i].Filter.box2DFilter.cloudRotation));
				m_fd.filter.groupIndex = tile.TileFixtures[i].Filter.box2DFilter.groupIndex;
				m_fd.filter.aboveBits = tile.TileFixtures[i].Filter.box2DFilter.aboveBits;
				m_fd.filter.isCloud = tile.TileFixtures[i].Filter.box2DFilter.isCloud;
				m_fd.filter.kickable = tile.Kickable & tile.TileFixtures[i].Filter.box2DFilter.kickable;
				m_fd.filter.kickableTop = tile.KickableTop & tile.TileFixtures[i].Filter.box2DFilter.kickableTop;
				m_fd.filter.punchable = tile.Punchable & tile.TileFixtures[i].Filter.box2DFilter.punchable;
				m_fd.filter.blockMelee = tile.Punchable & tile.TileFixtures[i].Filter.box2DFilter.blockMelee;
				m_fd.filter.blockFire = tile.TileFixtures[i].Filter.box2DFilter.blockFire;
				m_fd.filter.blockExplosions = material.BlockExplosions;
				m_fd.filter.projectileHit = tile.TileFixtures[i].ProjectileHit;
				m_fd.filter.absorbProjectile = tile.TileFixtures[i].AbsorbProjectile;
				m_fd.filter.objectStrength = tile.TileFixtures[i].ObjectStrength;
				if (spawnObject.IgnoreBodyID != 0)
				{
					m_fd.filter.bodyIDToIgnore = new Dictionary<int, ushort>();
					m_fd.filter.bodyIDToIgnore.Add(spawnObject.IgnoreBodyID, 1);
				}
				m_fd.shape = shape;
				Fixture fixture = body.CreateFixture(m_fd);
				if (tile.TileFixtures[i].Filter.mass != 0f)
				{
					fixture.SetMass(tile.TileFixtures[i].Filter.mass);
					body.ResetMassData();
				}
				fixture.ID = tile.TileFixtures[i].ID;
				fixture.TileFixtureIndex = (byte)i;
				fixture.SetUserData(objectData);
				objectData.AddOwner(fixture);
			}
			objectData.SetTile(tile);
			objectData.AddDecal(new ObjectDecal(objectData));
			body.AllowSleeping(flag: true);
			switch (tile.Type)
			{
			case Tile.TYPE.Dynamic:
				if (DynamicObjects.ContainsKey(objectData.ObjectID))
				{
					throw new Exception(string.Format("Error: Duplicate dynamic object ID key '{0}'. Object to create {1} - existing object {2}", objectData.ObjectID, objectData.MapObjectID, (DynamicObjects[objectData.ObjectID] == null) ? "null" : DynamicObjects[objectData.ObjectID].MapObjectID));
				}
				DynamicObjects.Add(objectData.ObjectID, objectData);
				if (DynamicBodies.ContainsKey(bodyDataFromObjectData.BodyID))
				{
					throw new Exception($"Error: Duplicate dynamic body ID key '{bodyDataFromObjectData.BodyID}'");
				}
				DynamicBodies.Add(bodyDataFromObjectData.BodyID, bodyDataFromObjectData);
				AddBodyToSync(bodyDataFromObjectData);
				break;
			default:
				throw new Exception("TODO: GameWorld.CreateTile.Tile.Type " + tile.Type);
			case Tile.TYPE.Static:
				if (StaticObjects.ContainsKey(objectData.ObjectID))
				{
					throw new Exception(string.Format("Error: Duplicate static object ID key '{0}'. Object to create {1} - existing object {2}", objectData.ObjectID, objectData.MapObjectID, (StaticObjects[objectData.ObjectID] == null) ? "null" : StaticObjects[objectData.ObjectID].MapObjectID));
				}
				StaticObjects.Add(objectData.ObjectID, objectData);
				if (StaticBodies.ContainsKey(bodyDataFromObjectData.BodyID))
				{
					throw new Exception($"Error: Duplicate body ID key '{bodyDataFromObjectData.BodyID}'");
				}
				StaticBodies.Add(bodyDataFromObjectData.BodyID, bodyDataFromObjectData);
				break;
			}
			body.SetLinearVelocity(spawnObject.InitialLinearVelocity);
			body.SetAngularVelocity(spawnObject.InitialAngularVelocity);
			objectData.SetGameWorld(this);
			while (RenderCategories[objectData.Tile.DrawCategory].TotalLayers <= spawnObject.Layer)
			{
				int totalLayers = RenderCategories[objectData.Tile.DrawCategory].TotalLayers;
				RenderCategories[objectData.Tile.DrawCategory].AddLayer(totalLayers);
				SetLayerPropertiesData(objectData.Tile.DrawCategory, totalLayers);
			}
			objectData.AddToRenderLayer(spawnObject.Layer);
			objectData.InitializeBase();
			objectData.Initialize();
			objectData.ApplyColors(spawnObject.Colors);
			objectData.Properties.Load(spawnObject.PropertyValues);
			objectData.SetInitialBodyType(SpawnObjectInformation.SpawnTypeValue.Default);
			objectData.InitializeCustomID();
			objectData.DrawFlag = true;
			objectData.SetGroupID(spawnObject.GroupID);
			if (Weather != null && tile.MainLayer == 1 && tile.Type == Tile.TYPE.Static)
			{
				WeatherSetCollisionAgainstBody(body);
			}
			if (newObjectData != null)
			{
				newObjectData.OriginalObjectData = objectData;
			}
			if (spawnObject.FireBurning && objectData.CanBurn)
			{
				objectData.Fire.IgnitionValue = objectData.Tile.Material.Resistance.Fire.Threshold;
				objectData.Fire.BurnTime = 5000f;
				objectData.Fire.SmokeTime = 3000f;
				objectData.StartTrackingFireValues();
			}
			else if (spawnObject.FireSmoking)
			{
				objectData.Fire.IgnitionValue = Math.Min(objectData.Tile.Material.Resistance.Fire.Threshold, 5f);
				objectData.Fire.SmokeTime = 3000f;
				objectData.StartTrackingFireValues();
			}
			PreserveJoints(spawnObject);
			CheckStoredObjectDataSyncedMethods(objectData);
			if (GameOwner != GameOwnerEnum.Client && !EditMode && objectData is ObjectTriggerBase)
			{
				CheckActivateOnStartupObjects.Add(objectData);
			}
			ObjectData.CreateNewCompleted(objectData);
			if (GameOwner == GameOwnerEnum.Client)
			{
				GroupSyncPrepareFinalization(objectData);
			}
			m_newlyCreatedObjects.Add(objectData);
			return body;
		}
		catch (Exception ex3)
		{
			throw new Exception("Error: Could not create tile of " + spawnObject.ToString() + "\r\n" + ex3.ToString());
		}
	}

	public void GroupSyncPrepareFinalization(ObjectData objectData)
	{
		if (objectData.GroupID > 0)
		{
			List<ObjectData> value = null;
			if (!m_groupSyncCreatedObjects.TryGetValue(objectData.GroupID, out value))
			{
				value = new List<ObjectData>();
				m_groupSyncCreatedObjects.Add(objectData.GroupID, value);
			}
			value.Add(objectData);
		}
	}

	public void GroupSyncFinilize(ushort groupID)
	{
		List<ObjectData> value = null;
		if (!m_groupSyncCreatedObjects.TryGetValue(groupID, out value))
		{
			return;
		}
		foreach (ObjectData item in value)
		{
			item.FinalizeProperties();
		}
		value.Clear();
		m_groupSyncCreatedObjects.Remove(groupID);
	}

	public void PreserveJoints(SpawnObjectInformation spawnObject)
	{
		ObjectData objectData = spawnObject.ObjectData;
		objectData.PreservedJointsFromObjectID = spawnObject.PreserveJointsFromObjectID;
		if (GameOwner == GameOwnerEnum.Client || spawnObject.PreserveJointsFromObjectID == 0)
		{
			return;
		}
		Dictionary<int, ObjectData>[] array = new Dictionary<int, ObjectData>[2] { StaticObjects, DynamicObjects };
		for (int i = 0; i < array.Length; i++)
		{
			foreach (KeyValuePair<int, ObjectData> item in array[i])
			{
				ObjectData value = item.Value;
				bool flag = false;
				foreach (ObjectPropertyInstance item2 in value.Properties.Items)
				{
					if (item2.Base.PropertyClass == ObjectPropertyClass.TargetObjectData)
					{
						if ((int)item2.Value == spawnObject.PreserveJointsFromObjectID)
						{
							item2.Value = objectData.ObjectID;
							flag = true;
						}
					}
					else
					{
						if (item2.Base.PropertyClass != ObjectPropertyClass.TargetObjectDataMultiple)
						{
							continue;
						}
						int[] array2 = Converter.StringToIntArray((string)item2.Value);
						bool flag2 = false;
						for (int j = 0; j < array2.Length; j++)
						{
							if (array2[j] == spawnObject.PreserveJointsFromObjectID)
							{
								array2[j] = objectData.ObjectID;
								flag2 = true;
								flag = true;
							}
						}
						if (flag2)
						{
							item2.Value = Converter.IntArrayToString(array2);
						}
					}
				}
				if (flag)
				{
					value.FinalizeProperties();
				}
			}
		}
	}

	public void CheckStoredObjectDataSyncedMethods(ObjectData objectData)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			for (ObjectDataSyncedMethod storedObjectDataSyncedMethod = GetStoredObjectDataSyncedMethod(objectData.ObjectID); storedObjectDataSyncedMethod != null; storedObjectDataSyncedMethod = GetStoredObjectDataSyncedMethod(objectData.ObjectID))
			{
				objectData.SyncedMethod(storedObjectDataSyncedMethod);
			}
		}
	}

	public void StoreObjectDataSyncedMethod(int objectID, ObjectDataSyncedMethod syncedMethod)
	{
		if (m_storedObjectDataSyncedMethods == null)
		{
			m_storedObjectDataSyncedMethods = new Dictionary<int, Queue<ObjectDataSyncedMethod>>();
		}
		if (!m_storedObjectDataSyncedMethods.ContainsKey(objectID))
		{
			m_storedObjectDataSyncedMethods.Add(objectID, new Queue<ObjectDataSyncedMethod>());
		}
		m_storedObjectDataSyncedMethods[objectID].Enqueue(syncedMethod);
	}

	public ObjectDataSyncedMethod GetStoredObjectDataSyncedMethod(int objectID)
	{
		if (m_storedObjectDataSyncedMethods == null)
		{
			return null;
		}
		if (!m_storedObjectDataSyncedMethods.ContainsKey(objectID))
		{
			return null;
		}
		ObjectDataSyncedMethod result = null;
		if (m_storedObjectDataSyncedMethods[objectID].Count > 0)
		{
			result = m_storedObjectDataSyncedMethods[objectID].Dequeue();
		}
		if (m_storedObjectDataSyncedMethods[objectID].Count == 0)
		{
			m_storedObjectDataSyncedMethods.Remove(objectID);
		}
		return result;
	}

	public void HandleQueuedNewObjects()
	{
		if (m_newlyCreatedObjects.Count <= 0)
		{
			return;
		}
		int count = m_newlyCreatedObjects.Count;
		for (int i = 0; i < count; i++)
		{
			ObjectData objectData = m_newlyCreatedObjects[i];
			if (!objectData.IsDisposed)
			{
				TriggerExplosionCheckNewCreatedObject(objectData);
			}
		}
		if (GameOwner != GameOwnerEnum.Client)
		{
			RunScriptOnObjectCreatedCallbacks(m_newlyCreatedObjects);
		}
		m_newlyCreatedObjects.Clear();
	}

	public Body CreateWeaponItem(SpawnObjectInformation spawnObject, bool useHolsteredModel, bool pickupable, bool enablePhysics)
	{
		Body body = CreateTile(spawnObject);
		ObjectData.Read(body).SyncedMethod(new ObjectDataSyncedMethod(ObjectDataSyncedMethod.Methods.SetWeaponItemProperties, ElapsedTotalGameTime, useHolsteredModel, pickupable, enablePhysics));
		return body;
	}

	public void SetGameInfo(GameInfo gameInfo)
	{
		GameInfo = gameInfo;
	}

	public int GetAlivePlayerCount()
	{
		int num = 0;
		for (int i = 0; i < Players.Count; i++)
		{
			if (!Players[i].IsDead)
			{
				num++;
			}
		}
		return num;
	}

	public List<int> GetAliveGameUserIdentifiers()
	{
		List<int> list = new List<int>();
		for (int i = 0; i < Players.Count; i++)
		{
			Player player = Players[i];
			if (!player.IsDead && player.GetGameUser() != null)
			{
				list.Add(player.UserIdentifier);
			}
		}
		return list;
	}

	public Microsoft.Xna.Framework.Vector2 GetPlayerSpawnPoint(Microsoft.Xna.Framework.Vector2 spawnPoint)
	{
		Tile tile = TileDatabase.Get("s_plr");
		if (tile == null)
		{
			throw new Exception("Missing required tile s_plr");
		}
		Filter plrFilter = tile.TileFixtures[0].Filter.box2DFilter;
		Microsoft.Xna.Framework.Vector2 vector = spawnPoint;
		Filter fixtureFilter;
		RayCastResult rayCastResult = RayCast(vector - Microsoft.Xna.Framework.Vector2.UnitX * 2f, -Microsoft.Xna.Framework.Vector2.UnitY, 0f, 32f, delegate(Fixture fixture)
		{
			fixture.GetFilterData(out fixtureFilter);
			return Settings.b2ShouldCollide(ref plrFilter, ref fixtureFilter);
		}, (Player player) => false);
		RayCastResult rayCastResult2 = RayCast(vector + Microsoft.Xna.Framework.Vector2.UnitX * 2f, -Microsoft.Xna.Framework.Vector2.UnitY, 0f, 32f, delegate(Fixture fixture)
		{
			fixture.GetFilterData(out fixtureFilter);
			return Settings.b2ShouldCollide(ref plrFilter, ref fixtureFilter);
		}, (Player player) => false);
		RayCastResult rayCastResult3 = rayCastResult;
		Microsoft.Xna.Framework.Vector2 vector2 = Microsoft.Xna.Framework.Vector2.UnitX * 2f;
		if (rayCastResult3.EndFixture == null || (rayCastResult2.EndFixture != null && rayCastResult2.Fraction < rayCastResult3.Fraction))
		{
			rayCastResult3 = rayCastResult2;
			vector2 = -Microsoft.Xna.Framework.Vector2.UnitX * 2f;
		}
		if (rayCastResult3.EndFixture != null)
		{
			vector = rayCastResult3.EndPosition + vector2 + Microsoft.Xna.Framework.Vector2.UnitY * 4f;
		}
		return vector;
	}

	public float GetElapsedLocalGameTimeSinceTimestamp(float timestamp)
	{
		if (m_elapsedTotalGameTimeTimestamps == null)
		{
			m_elapsedTotalGameTimeTimestamps = new Dictionary<float, float>();
		}
		if (!m_elapsedTotalGameTimeTimestamps.ContainsKey(timestamp))
		{
			m_elapsedTotalGameTimeTimestamps.Add(timestamp, ElapsedTotalGameTime);
		}
		return ElapsedTotalGameTime - m_elapsedTotalGameTimeTimestamps[timestamp];
	}

	public ObjectData CreateObjectData(string mapObjectID)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: Only the server / local can call GameWorld.CreateObjectData()");
		}
		return IDCounter.NextObjectData(mapObjectID);
	}

	public ObjectData CreateObjectData(string mapObjectID, int objectID)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: Only the server / local can call GameWorld.CreateObjectData()");
		}
		return ObjectData.CreateNew(new ObjectDataStartParams(objectID, 0, 0, mapObjectID, GameOwner));
	}

	public ObjectData CreateObjectData(string mapObjectID, int customID, int objectID)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: Only the server / local can call GameWorld.CreateObjectData()");
		}
		return ObjectData.CreateNew(new ObjectDataStartParams(objectID, customID, 0, mapObjectID, GameOwner));
	}

	public GameWorld(GameSFD game, GameOwnerEnum gameOwner)
	{
		if (gameOwner != GameOwnerEnum.Server)
		{
			PopupMessage.ClearAndHide();
		}
		WorldNr = Interlocked.Increment(ref m_uniqueGameWorldID);
		WorldID = $"{WorldNr:000000}";
		IDCounter = new IDCounter(gameOwner);
		m_game = game;
		GameOwner = gameOwner;
		AllowDisposeOfScripts = false;
		AutoVictoryConditionEnabled = true;
		AutoScoreConditionEnabled = true;
		IgnoreTimers = false;
		m_activeSecondaryCameraTargetsAll = new List<ObjectData>(10);
		m_localGameUserPlayerCache = new CachedPlayerKey(this, useUserIdentifier: true);
		ElapsedTotalGameTime = 0f;
		ElapsedTotalRealTime = 0f;
		MuteSounds = false;
		GameOverData = new GameOverResultData(gameOwner);
		WaterZones = new List<ObjectData>();
		Streetsweepers = new List<ObjectStreetsweeper>();
		OnPlayerDeathTriggers = new List<ObjectOnPlayerDeathTrigger>();
		OnPlayerDamageTriggers = new List<ObjectOnPlayerDamageTrigger>();
		OnGameOverTriggers = new List<ObjectOnGameOverTrigger>();
		ScriptBridge = new GameWorldScriptBridge(this);
		ExtensionScripts = new Dictionary<string, SandboxInstance>();
		ExtensionScriptsUniqueID = new Dictionary<string, string>();
		CallingScriptInstance = new Stack<SandboxInstance>();
		m_groupSyncCreatedObjects = new Dictionary<ushort, List<ObjectData>>();
		m_queueScriptBridgeDispose = new List<IObject>();
		ObjectColorUpdates = new List<ObjectData>();
		m_activeObjectCameraAreaTrigger = new CachedObjectData<ObjectCameraAreaTrigger>(this, continousLookupWhenNotFound: true);
		m_worldCameraPackage = new CameraAreaPackage();
		m_debrisNew = new List<ObjectData>();
		m_debrisCurrent = new List<ObjectData>();
		m_AITargetableObjects = new List<ObjectData>();
		BringPlayerToFront = new List<Player>();
		WorldCameraSourceArea = new Area(240f, -320f, -240f, 320f);
		BoundsWorldBottom = -320;
		RecalculateWorldCameraAreas();
		InLoading = false;
		DisposedObjectIDs = new HashSet<int>();
		PathGrid = new SFD.PathGrid.PathGrid(this);
		CameraFocusPoints = new List<CameraFocusPoint>(10);
		NewObjectsCollection = new NewObjectCollection();
		CheckActivateOnStartupObjects = new List<ObjectData>(10);
		ObjectSyncedPositionUpdates = new List<BodyData>(10);
		ObjectForcedPositionUpdates = new List<BodyData>(2);
		ObjectPositionUpdates = new Dictionary<Body, ObjectPositionUpdateInfo>(80);
		ObjectCleanUpdates = new List<ObjectData>(10);
		ObjectUpdateCycleUpdates = new List<ObjectData>(80);
		ObjectUpdateCycleUpdatesToRemove = new List<ObjectData>(10);
		ObjectUpdateCycleUpdatesToAdd = new List<ObjectData>(10);
		SlowmotionHandler = new SlowmotionHandler(this);
		ObjectPropertyValuesToSend = new List<ObjectPropertyInstance>(4);
		PlayerAIPackages = new Queue<PlayerAIPackage>();
		CustomIDTableLookup = new Dictionary<string, int>();
		InitializeGibbingData();
		InitializeDialogueData();
		EditReadOnly = false;
		IsExtensionScript = false;
		EditChangesMade = false;
		EditDrawCenter = true;
		EditDrawWorldZones = true;
		EditDrawGrid = true;
		EditDrawPathGrid = true;
		EditGridSize = 8;
		EditSnapToGrid = true;
		EditMode = false;
		EditPhysicsRunning = false;
		EditMouseOffset = Microsoft.Xna.Framework.Vector2.Zero;
		EditSnapMode = EditSnapMode.TopLeft;
		EditOffsetPositions = new List<Microsoft.Xna.Framework.Vector2>();
		EditSelectedObjects = new List<ObjectData>();
		EditPreviewObjects = new List<ObjectData>();
		EditHighlightObjectsOnce = new List<ObjectData>();
		EditHighlightObjectsFixed = new List<Tuple<ObjectData, ObjectData>>();
		EditCursor = EditorCurors.Default;
		EditSelectionArea = new SelectionArea();
		m_editHistoryActions = new List<List<EditHistoryItemBase>>();
		m_editHistoryActionsUndone = new List<List<EditHistoryItemBase>>();
		SyncedTileAnimations = new Dictionary<string, TileAnimation>();
		m_clientHeathChecks = new List<ObjectData>();
		PlayersLookup = new Dictionary<int, Player>(10);
		Players = new List<Player>(10);
		Projectiles = new List<Projectile>(50);
		NewProjectiles = new List<Projectile>(10);
		OldProjectiles = new List<int>(10);
		InfoObjects = new List<ObjectData>(40);
		BotSearchItems = new List<ObjectData>(40);
		DynamicObjects = new Dictionary<int, ObjectData>(200);
		DynamicBodies = new Dictionary<int, BodyData>(200);
		PlayerSpawnMarkers = new List<ObjectPlayerSpawnMarker>(16);
		BodiesToSync = new List<BodyData>();
		MissileUpdateObjects = new List<ObjectData>();
		StaticObjects = new Dictionary<int, ObjectData>(500);
		StaticBodies = new Dictionary<int, BodyData>(500);
		RenderCategories = new SFDRenderCategories<ObjectData>(30);
		InitGameTimers();
		Portals = new List<GameWorldPortal>();
		PortalsObjectsToKeepTrackOf = new List<ObjectData>();
		RemovedProjectiles = new List<Projectile>(2);
		TriggerMineObjectsToKeepTrackOf = new List<ObjectData>();
		GroupInfo = new Dictionary<ushort, GroupInfo>();
		GroupSpawnInfo = new Dictionary<ushort, GroupSpawnInfo>();
		m_recentExplosions = new List<ExplosionData>();
		m_queuedExplosions = new Queue<ItemContainer<Microsoft.Xna.Framework.Vector2, float, bool, int>>();
		PlayingUsersVersusMode = PlayingUserMode.None;
		m_meleePlayersList = new List<Player>(8);
		m_kickPlayersList = new List<Player>(8);
		InitMultiPacket();
		if (GameOwner != GameOwnerEnum.Server)
		{
			GameEffects = new GameEffects();
		}
		InitQueueTriggers();
		InitKickedObjects();
		Microsoft.Xna.Framework.Vector2 gravity = new Microsoft.Xna.Framework.Vector2(0f, -26f);
		b2_world_active = new World(gravity, doSleep: true);
		b2_world_active.UserData = this;
		b2_world_active.ContactFilter = new WorldContactFilter(this);
		b2_world_active.ContactListener = new WorldContactListener(this);
		b2_world_active.DestructionListener = new WorldDestructionListener(this);
		b2_world_background = new World(gravity, doSleep: true);
		b2_world_background.UserData = this;
		b2_world_background.ContactFilter = new WorldContactFilter(this);
		b2_world_background.ContactListener = new WorldContactListener(this);
		b2_world_background.DestructionListener = new WorldDestructionListener(this);
		b2_settings = new Box2DSettings();
		b2_settings.drawAABBs = 0u;
		b2_settings.drawShapes = 1u;
		ObjectWorldData = (ObjectWorld)ObjectData.CreateNew(new ObjectDataStartParams(2147483646, 0, 0, "world", GameOwner));
		ObjectWorldData.SetGameWorld(this);
		PropertiesWorld = ObjectWorldData.Properties;
		b2_debugDraw = new DebugDraw();
		b2_world_active.DebugDraw = b2_debugDraw;
		b2_world_background.DebugDraw = b2_debugDraw;
		lock (Program.DRIVER_LOCKLESS ? this : GameSFD.SpriteBatchResourceObject)
		{
			Utils.WaitForGraphicsDevice();
			b2_simpleColorEffect = new BasicEffect(m_graphicsDevice);
			b2_simpleColorEffect.VertexColorEnabled = true;
			b2_vertexDecl = new VertexDeclaration(VertexPositionTexture.VertexDeclaration.GetVertexElements());
			DebugDraw._batch = m_spriteBatch;
			DebugDraw._device = m_graphicsDevice;
			DebugDraw._font = Constants.DebugFont;
		}
		ToggleDrawAABBs(0u);
		ToggleDrawShapes(0u);
		if (GameOwner != GameOwnerEnum.Client)
		{
			WeaponSpawnManagerOLD = new WeaponSpawnManagerOLD(this);
			WeaponSpawnManager = new WeaponSpawnManager(this);
		}
		CreateFireGrid();
		LoadGUI();
		if (gameOwner != GameOwnerEnum.Server)
		{
			SetCameraMode(Constants.INITIAL_CAMERA_MODE);
		}
	}

	~GameWorld()
	{
	}

	public UserScriptBridge GetUserScriptBridge(int userIdentifier)
	{
		GameUser gameUser = GameInfo.GetGameUserByUserIdentifier(userIdentifier);
		if (gameUser != null)
		{
			UserScriptBridge userScriptBridge = UserScriptBridges.Find((UserScriptBridge x) => x.UserIdentifier == gameUser.UserIdentifier);
			if (userScriptBridge != null)
			{
				return userScriptBridge;
			}
			return new UserScriptBridge(this, gameUser);
		}
		return null;
	}

	public void QueueScriptBridgeDispose(IObject scriptBridge)
	{
		m_queueScriptBridgeDispose.Add(scriptBridge);
	}

	public void DisposeQueuedScriptBridges()
	{
		if (m_queueScriptBridgeDispose == null || m_queueScriptBridgeDispose.Count <= 0)
		{
			return;
		}
		foreach (IObject item in m_queueScriptBridgeDispose)
		{
			item.Dispose();
		}
		m_queueScriptBridgeDispose.Clear();
	}

	public void DisposeAllObjects()
	{
		m_queuedTriggers.Clear();
		m_activatedTriggers.Clear();
		m_queuedTriggersWithSenders.Clear();
		EditHistoryClearAll();
		List<ObjectData> list = new List<ObjectData>();
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			list.Add(dynamicObject.Value);
		}
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			list.Add(staticObject.Value);
		}
		foreach (ObjectData item in list)
		{
			item.Dispose();
		}
		foreach (KeyValuePair<ushort, GroupSpawnInfo> item2 in GroupSpawnInfo)
		{
			item2.Value.Dispose(this);
		}
		GroupSpawnInfo.Clear();
		foreach (Player player in Players)
		{
			player.Dispose();
		}
		Players.Clear();
		foreach (ObjectData objectCleanUpdate in ObjectCleanUpdates)
		{
			objectCleanUpdate.Dispose();
		}
		ObjectCleanUpdates.Clear();
		list.Clear();
		list = null;
		for (int i = 0; i < RenderCategories.Categories.Length; i++)
		{
			for (int j = 0; j < RenderCategories.Categories[i].TotalLayers; j++)
			{
				((SFDLayerTag)RenderCategories[i].GetLayer(j).Tag)?.Dispose();
			}
		}
		DynamicObjects.Clear();
		DynamicBodies.Clear();
		StaticObjects.Clear();
		StaticBodies.Clear();
		BodiesToSync.Clear();
	}

	public void Dispose()
	{
		lock (m_disposeLockObject)
		{
			m_disposed = true;
			DisposeMultiPacket();
			DisposeAllCallbacksForAllScripts();
			LocalPlayers[0] = null;
			LocalPlayers[1] = null;
			LocalPlayers[2] = null;
			LocalPlayers[3] = null;
			LocalGameUsers[0] = null;
			LocalGameUsers[1] = null;
			LocalGameUsers[2] = null;
			LocalGameUsers[3] = null;
			PrimaryLocalPlayer = null;
			m_localGameUserPlayerCache.Dispose();
			StopScripts();
			ObjectColorUpdates.Clear();
			DisposeAllObjects();
			if (m_activeObjectCameraAreaTrigger != null)
			{
				m_activeObjectCameraAreaTrigger.Dispose();
				m_activeObjectCameraAreaTrigger = null;
			}
			if (m_activeSecondaryCameraTargetsAll != null)
			{
				m_activeSecondaryCameraTargetsAll.Clear();
				m_activeSecondaryCameraTargetsAll = null;
			}
			DisposeQueuedScriptBridges();
			if (GameOwner != GameOwnerEnum.Server)
			{
				PopupMessage.ClearAndHide();
			}
			DisposeGibbingData();
			DisposeDialogueData();
			DisposeQueueTriggers();
			DisposeKickedObjects();
			EditHistoryClearAll();
			if (Weather != null)
			{
				Weather.Remove();
				Weather = null;
			}
			DisposeGameTimers();
			SetGameInfo(null);
			if (ScriptBridge != null)
			{
				ScriptBridge.Dispose();
				ScriptBridge = null;
			}
			if (EditMode)
			{
				EditSelectedObjects = null;
				EditPreviewObjects = null;
			}
			EditHighlightObjectsOnce = null;
			EditHighlightObjectsFixed = null;
			DisposeFireGrid();
			if (WeaponSpawnManager != null)
			{
				WeaponSpawnManager.Dispose();
				WeaponSpawnManager = null;
			}
			SlowmotionHandler.Dispose();
			ObjectWorldData.Dispose();
			ObjectWorldData = null;
			PropertiesWorld = null;
			CustomIDTableLookup.Clear();
			MissileUpdateObjects.Clear();
			MissileUpdateObjects = null;
			m_debrisNew.Clear();
			m_debrisNew = null;
			m_debrisCurrent.Clear();
			m_debrisCurrent = null;
			if (m_newlyCreatedObjects != null)
			{
				m_newlyCreatedObjects.Clear();
				m_newlyCreatedObjects = null;
			}
			if (m_newlyCreatedPlayers != null)
			{
				m_newlyCreatedPlayers.Clear();
				m_newlyCreatedPlayers = null;
			}
			CheckActivateOnStartupObjects.Clear();
			CheckActivateOnStartupObjects = null;
			NewObjectsCollection.Dispose();
			foreach (KeyValuePair<Body, ObjectPositionUpdateInfo> objectPositionUpdate in ObjectPositionUpdates)
			{
				objectPositionUpdate.Value.posData.FlagAsFree();
			}
			ObjectPositionUpdates.Clear();
			ObjectPositionUpdates = null;
			ObjectSyncedPositionUpdates.Clear();
			ObjectSyncedPositionUpdates = null;
			ObjectForcedPositionUpdates.Clear();
			ObjectForcedPositionUpdates = null;
			ObjectCleanUpdates.Clear();
			ObjectCleanUpdates = null;
			ObjectUpdateCycleUpdates.Clear();
			ObjectUpdateCycleUpdates = null;
			ObjectUpdateCycleUpdatesToAdd.Clear();
			ObjectUpdateCycleUpdatesToAdd = null;
			ObjectUpdateCycleUpdatesToRemove.Clear();
			ObjectUpdateCycleUpdatesToRemove = null;
			SlowmotionHandler = null;
			IDCounter = null;
			ObjectPropertyValuesToSend.Clear();
			ObjectPropertyValuesToSend = null;
			WaterZones.Clear();
			WaterZones = null;
			Streetsweepers.Clear();
			Streetsweepers = null;
			OnPlayerDeathTriggers.Clear();
			OnPlayerDeathTriggers = null;
			OnPlayerDamageTriggers.Clear();
			OnPlayerDamageTriggers = null;
			OnGameOverTriggers.Clear();
			OnGameOverTriggers = null;
			PlayerAIPackages.Clear();
			PlayerAIPackages = null;
			EditOffsetPositions = new List<Microsoft.Xna.Framework.Vector2>();
			EditSelectedObjects = new List<ObjectData>();
			EditPreviewObjects = new List<ObjectData>();
			EditCursor = EditorCurors.Default;
			EditSelectionArea = new SelectionArea();
			m_editHistoryActions = new List<List<EditHistoryItemBase>>();
			m_editHistoryActionsUndone = new List<List<EditHistoryItemBase>>();
			PlayersLookup.Clear();
			PlayersLookup = null;
			Players.Clear();
			Players = null;
			InfoObjects.Clear();
			InfoObjects = null;
			BotSearchItems.Clear();
			BotSearchItems = null;
			Projectiles.Clear();
			Projectiles = null;
			foreach (Projectile newProjectile in NewProjectiles)
			{
				newProjectile.Dispose();
			}
			NewProjectiles.Clear();
			NewProjectiles = null;
			OldProjectiles.Clear();
			OldProjectiles = null;
			foreach (Projectile removedProjectile in RemovedProjectiles)
			{
				removedProjectile.Dispose();
			}
			RemovedProjectiles.Clear();
			RemovedProjectiles = null;
			m_recentExplosions.Clear();
			m_recentExplosions = null;
			m_queuedExplosions.Clear();
			m_queuedExplosions = null;
			DynamicObjects.Clear();
			DynamicBodies.Clear();
			BodiesToSync.Clear();
			StaticObjects.Clear();
			StaticBodies.Clear();
			DynamicObjects = null;
			DynamicBodies = null;
			BodiesToSync = null;
			StaticObjects = null;
			StaticBodies = null;
			RenderCategories.Clear();
			RenderCategories = null;
			if (m_storedObjectDataSyncedMethods != null)
			{
				m_storedObjectDataSyncedMethods.Clear();
				m_storedObjectDataSyncedMethods = null;
			}
			if (m_queuedRunScriptOnObjectDamageCallbacks != null)
			{
				m_queuedRunScriptOnObjectDamageCallbacks.Clear();
				m_queuedRunScriptOnObjectDamageCallbacks = null;
			}
			Portals.Clear();
			Portals = null;
			GroupSpawnInfo.Clear();
			GroupSpawnInfo = null;
			GroupInfo.Clear();
			GroupInfo = null;
			m_meleePlayersList.Clear();
			m_meleePlayersList = null;
			m_kickPlayersList.Clear();
			m_kickPlayersList = null;
			if (m_elapsedTotalGameTimeTimestamps != null)
			{
				m_elapsedTotalGameTimeTimestamps.Clear();
				m_elapsedTotalGameTimeTimestamps = null;
			}
			if (CameraFocusPoints != null)
			{
				CameraFocusPoints.Clear();
				CameraFocusPoints = null;
			}
			DisposeDebugPathNodeData();
			if (PathGrid != null)
			{
				PathGrid.Dispose();
				PathGrid = null;
			}
			if (GameEffects != null)
			{
				GameEffects.Dispose();
				GameEffects = null;
			}
			if (WeaponSpawnManagerOLD != null)
			{
				WeaponSpawnManagerOLD.Dispose();
				WeaponSpawnManagerOLD = null;
			}
			if (b2_world_active != null)
			{
				((WorldContactFilter)b2_world_active.ContactFilter).Dispose();
				((WorldContactListener)b2_world_active.ContactListener).Dispose();
				b2_world_active.Dispose();
				b2_world_active = null;
			}
			if (b2_world_background != null)
			{
				((WorldContactFilter)b2_world_background.ContactFilter).Dispose();
				((WorldContactListener)b2_world_background.ContactListener).Dispose();
				b2_world_background.Dispose();
				b2_world_background = null;
			}
			if (b2_simpleColorEffect != null)
			{
				b2_simpleColorEffect.Dispose();
				b2_simpleColorEffect = null;
			}
			b2_settings = null;
			m_multiPacket = null;
			m_game = null;
			b2_debugDraw = null;
			if (b2_vertexDecl != null)
			{
				b2_vertexDecl.Dispose();
				b2_vertexDecl = null;
			}
			if (m_worldCameraPackage != null)
			{
				m_worldCameraPackage.Dispose();
				m_worldCameraPackage = null;
			}
			WorldCameraSourceArea = null;
			WorldCameraSafeArea = null;
			WorldCameraMaxArea = null;
			if (BringPlayerToFront != null)
			{
				BringPlayerToFront.Clear();
			}
			BringPlayerToFront = null;
			CallingScriptInstance = null;
			if (CustomIDTableLookup != null)
			{
				CustomIDTableLookup.Clear();
			}
			CustomIDTableLookup = null;
			DefaultScript = null;
			if (EditOffsetPositions != null)
			{
				EditOffsetPositions.Clear();
			}
			EditOffsetPositions = null;
			if (EditPreviewObjects != null)
			{
				EditPreviewObjects.Clear();
			}
			EditPreviewObjects = null;
			if (EditSelectedObjects != null)
			{
				EditSelectedObjects.Clear();
			}
			EditSelectedObjects = null;
			EditSelectionArea = null;
			if (ExtensionScripts != null)
			{
				ExtensionScripts.Clear();
			}
			ExtensionScripts = null;
			if (ExtensionScriptsUniqueID != null)
			{
				ExtensionScriptsUniqueID.Clear();
			}
			ExtensionScriptsUniqueID = null;
			m_editSelectionFitler = null;
			if (m_groupSyncCreatedObjects != null)
			{
				m_groupSyncCreatedObjects.Clear();
			}
			m_groupSyncCreatedObjects = null;
			m_fd = null;
			m_bd = null;
			if (m_queuedPlayerReceiveItemUpdates != null)
			{
				m_queuedPlayerReceiveItemUpdates.Clear();
			}
			m_queuedPlayerReceiveItemUpdates = null;
			if (m_queuedPlayerMetaDataUpdates != null)
			{
				m_queuedPlayerMetaDataUpdates.Clear();
			}
			m_queuedPlayerMetaDataUpdates = null;
			m_victoryText = null;
			m_voteCounter = null;
			m_transitionCircle = null;
			if (PlayerSpawnMarkers != null)
			{
				PlayerSpawnMarkers.Clear();
			}
			PlayerSpawnMarkers = null;
			if (PortalsObjectsToKeepTrackOf != null)
			{
				PortalsObjectsToKeepTrackOf.Clear();
			}
			PortalsObjectsToKeepTrackOf = null;
			if (TriggerMineObjectsToKeepTrackOf != null)
			{
				TriggerMineObjectsToKeepTrackOf.Clear();
			}
			TriggerMineObjectsToKeepTrackOf = null;
			if (SyncedTileAnimations != null)
			{
				SyncedTileAnimations.Clear();
			}
			SyncedTileAnimations = null;
			if (m_gibForces != null)
			{
				m_gibForces.Clear();
			}
			m_gibForces = null;
			if (DisposedObjectIDs != null)
			{
				DisposedObjectIDs.Clear();
			}
			DisposedObjectIDs = null;
			GameOverData = null;
			if (m_startupText != null)
			{
				m_startupText.Dispose();
			}
			m_startupText = null;
			for (int i = 0; i < PlayerHUDs.Length; i++)
			{
				PlayerHUDs[i].Dispose();
				PlayerHUDs[i] = null;
			}
			m_editResizeDataBefore = null;
			m_editHistoryActions = null;
			m_editHistoryActionsUndone = null;
			m_editPreviewOffsets = null;
			m_editRotationStatusesBeforeRotate = null;
			m_editSelectionPositionBeforeMove = null;
			if (m_AITargetableObjects != null)
			{
				m_AITargetableObjects.Clear();
				m_AITargetableObjects = null;
			}
		}
	}

	public void AddBodyToSync(BodyData bd)
	{
		if (!BodiesToSync.Contains(bd))
		{
			BodiesToSync.Add(bd);
		}
	}

	public void RemoveBodyToSync(BodyData bd)
	{
		BodiesToSync.Remove(bd);
		ObjectForcedPositionUpdates.Remove(bd);
	}

	public void ToggleDrawAABBs()
	{
		b2_settings.drawAABBs = ((b2_settings.drawAABBs == 0) ? 1u : 0u);
		b2_flagsSet = false;
	}

	public void ToggleDrawShapes()
	{
		b2_settings.drawShapes = ((b2_settings.drawShapes == 0) ? 1u : 0u);
		b2_flagsSet = false;
	}

	public void ToggleDrawAABBs(uint value)
	{
		b2_settings.drawAABBs = value;
		b2_flagsSet = false;
	}

	public void ToggleDrawShapes(uint value)
	{
		b2_settings.drawShapes = value;
		b2_flagsSet = false;
	}

	public void PositionElements()
	{
		m_voteCounter.PositionElements();
		m_victoryText.PositionElements();
		if (Weather != null)
		{
			Weather.SetGameSize(GameSFD.GAME_WIDTH, GameSFD.GAME_HEIGHT);
		}
	}

	public static void UpdateGameWorld(float time, GameWorldUpdateValues updateValues, UpdateGameWorldRun updateGameWorldRun)
	{
		if (time > 200f)
		{
			time = 200f;
		}
		float num = time % Constants.PREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE;
		time -= num;
		if (!(time >= Constants.PREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE))
		{
			return;
		}
		float num2 = (int)Math.Max(time / Constants.PREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE, 1f);
		if (updateValues.CurrentUpdateFPS < Constants.PREFFERED_GAMEWORLD_SIMULATION_UPDATE_FPS)
		{
			num2 = Math.Max(Math.Min(num2, updateValues.CurrentUpdateFPS * (time * 0.001f)), 1f);
			if (time / (float)(int)(updateValues.CurrentUpdateIterations + num2) > Constants.MAX_GAMEWORLD_SIMULATION_CHUNK_SIZE)
			{
				num2 = (float)Math.Ceiling(time / Constants.MAX_GAMEWORLD_SIMULATION_CHUNK_SIZE) - updateValues.CurrentUpdateIterations + 1E-05f;
			}
		}
		updateValues.CurrentUpdateIterations += num2;
		int num3 = (int)updateValues.CurrentUpdateIterations;
		if (num3 > 50)
		{
			num3 = 50;
		}
		updateValues.CurrentUpdateIterations -= num3;
		float chunkSizeTime = time / (float)num3;
		if (updateValues.CurrentUpdateFPS < 10f)
		{
			updateValues.CurrentUpdateIterations -= num2;
			updateValues.CurrentUpdateIterations += num3;
			num2 = 1000f / updateValues.CurrentUpdateIterationTime;
			num2 *= updateValues.CurrentUpdateFPS / 10f;
			num2 *= time / 1000f;
			updateValues.CurrentUpdateIterations += num2;
			num3 = (int)updateValues.CurrentUpdateIterations;
			updateValues.CurrentUpdateIterations -= num3;
			chunkSizeTime = Math.Min(time / (float)num3, 30f);
		}
		float num4 = Math.Min(updateGameWorldRun(num3, chunkSizeTime, time), 200f);
		if (num4 >= 1f)
		{
			float num5 = 1000f / num4;
			updateValues.CurrentUpdateFPS = (updateValues.CurrentUpdateFPS * 3f + num5) * 0.25f;
			updateValues.CurrentUpdateIterationTime = (updateValues.CurrentUpdateIterationTime * 3f + num4 / (float)num3) * 0.25f;
		}
		else
		{
			updateValues.CurrentUpdateFPS = (updateValues.CurrentUpdateFPS * 3f + Constants.PREFFERED_GAMEWORLD_SIMULATION_UPDATE_FPS) * 0.25f;
			updateValues.CurrentUpdateIterationTime = (updateValues.CurrentUpdateIterationTime * 3f + 1f / (float)num3) * 0.25f;
		}
		updateValues.UpdateThreadTimeLastValue = updateValues.UpdateThreadTime - num;
	}

	public void PerformFirstTimePosition()
	{
		float pREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE = Constants.PREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE;
		b2_settings.timeStep = pREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE * 0.85f * 0.001f;
		Step(b2_settings);
		m_box2DsimulationTimeOver -= pREFFERED_GAMEWORLD_SIMULATION_CHUNK_SIZE;
	}

	public void Update(float chunkMs, float totalMs, bool isLast, bool isFirst)
	{
		ElapsedTotalRealTime += chunkMs;
		if (IsCorrupted)
		{
			if (!isLast)
			{
				return;
			}
			if (GameSFD.Handle.CurrentState == State.EditorTestRun)
			{
				GameSFD.Handle.ChangeState(State.Editor);
			}
			else if (GameSFD.Handle.CurrentState == State.DSHome)
			{
				if (ElapsedTotalRealTime > 1000f)
				{
					GameSFD.Handle.Server?.SendClientChatMessageToAll(Server.LocalServerUser, "An unexpected script error occured. Restarting map.", Microsoft.Xna.Framework.Color.Red);
					RunGameCommand("/r", startup: false, forceRunCommand: true);
				}
			}
			else if (GameSFD.Handle.CurrentState == State.Game)
			{
				Server server = GameSFD.Handle.Server;
				if (server != null)
				{
					if (ElapsedTotalRealTime > 1000f)
					{
						server.SendClientChatMessageToAll(Server.LocalServerUser, "An unexpected script error occured. Restarting map.", Microsoft.Xna.Framework.Color.Red);
						RunGameCommand("/r", startup: false, forceRunCommand: true);
					}
				}
				else
				{
					MessageStack.Show("An unexpected script error occured.", MessageStackType.Error);
					GameSFD.Handle.ChangeState(State.MainMenu);
				}
			}
			else
			{
				_ = GameSFD.Handle.CurrentState;
			}
			return;
		}
		float ms = chunkMs;
		float num = totalMs;
		chunkMs *= SlowmotionHandler.SlowmotionModifier;
		totalMs *= SlowmotionHandler.SlowmotionModifier;
		ElapsedTotalGameTime += chunkMs;
		UpdateCameraAreaInfo();
		m_lastUpdateTime = chunkMs;
		if (Program.IsGame && (EditMode & !EditPhysicsRunning))
		{
			try
			{
				HandleObjectCleanCycle();
				return;
			}
			catch (GameWorldCorruptedException)
			{
				return;
			}
			catch (Exception ex2)
			{
				m_game.ShowError("Error: GameWorld.Update.HandleCleanCycle() during edit \r\n" + ex2.ToString());
				return;
			}
		}
		if (isLast)
		{
			UpdateLocalPlayerVirtualInput();
		}
		UpdateKickedObjects();
		SlowmotionHandler.Progress(ms);
		CheckMainTimer();
		if (Program.IsGame && isLast && GameOwner != GameOwnerEnum.Client && (m_game.CurrentState == State.EditorTestRun || m_game.CurrentState == State.MainMenu))
		{
			UpdateDebugMouse();
		}
		if (!m_firstUpdateIsRun)
		{
			try
			{
				OnStartup();
			}
			catch (GameWorldCorruptedException)
			{
				return;
			}
			catch (Exception ex4)
			{
				m_game.ShowError("Error: GameWorld.Update.OnStartup\r\n" + ex4.ToString());
				return;
			}
		}
		if (isFirst)
		{
			HandleCheckActivateOnStartupObjects();
			if (GameOwner != GameOwnerEnum.Server)
			{
				RemovedProjectiles.Clear();
			}
			m_explosionsTriggeredThisCycle = 0;
			try
			{
				for (int i = 0; i < Players.Count; i++)
				{
					Player player = Players[i];
					if (!player.IsDisposed && !player.IsRemoved)
					{
						if (GameOwner != GameOwnerEnum.Client)
						{
							player.UpdateAI(totalMs);
						}
						player.HandlePlayerKeyHoldingPreUpdateEvent();
						player.VirtualKeyboard.CheckFlippedMovemet();
					}
				}
				ProcessPlayerAIPackages();
			}
			catch (Exception ex5)
			{
				m_game.ShowError("Error: GameWorld.Update players (AI) failed\r\n" + ex5.ToString());
				return;
			}
		}
		try
		{
			HandleQueuedTriggers(chunkMs);
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex7)
		{
			m_game.ShowError("Error: GameWorld.Update.HandleQueuedTriggers\r\n" + ex7.ToString());
			return;
		}
		try
		{
			RunScriptUpdateCallbacks();
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex9)
		{
			m_game.ShowError("Error: GameWorld.Update.RunScriptUpdateCallbacks() \r\n" + ex9.ToString());
			return;
		}
		try
		{
			HandleObjectUpdateCycle(chunkMs);
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex11)
		{
			m_game.ShowError("Error: GameWorld.Update.HandleObjectUpdateCycle() \r\n" + ex11.ToString());
			return;
		}
		HandleQueuedExplosions();
		try
		{
			HandleObjectCleanCycle();
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex13)
		{
			m_game.ShowError("Error: GameWorld.Update.HandleCleanCycle() \r\n" + ex13.ToString());
			return;
		}
		try
		{
			HandleQueuedNewObjects();
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex15)
		{
			m_game.ShowError("Error: GameWorld.Update.HandleQueuedNewObjects() \r\n" + ex15.ToString());
			return;
		}
		try
		{
			HandleQueuedNewPlayers();
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex17)
		{
			m_game.ShowError("Error: GameWorld.Update.HandleQueuedNewPlayers() \r\n" + ex17.ToString());
			return;
		}
		if (isLast)
		{
			UpdateRecentExplosions(num);
		}
		try
		{
			HandlePositionUpdates();
		}
		catch (Exception ex18)
		{
			m_game.ShowError("Error: GameWorld.Update.HandlePositionUpdates() \r\n" + ex18.ToString());
			return;
		}
		try
		{
			UpdatePortals();
		}
		catch (Exception ex19)
		{
			m_game.ShowError("Error: GameWorld.HandlePortalUpdates() \r\n" + ex19.ToString());
			return;
		}
		if (isLast)
		{
			if (GameOwner != GameOwnerEnum.Client)
			{
				try
				{
					UpdateDebris();
				}
				catch (Exception ex20)
				{
					m_game.ShowError("Error: GameWorld.UpdateDebris() \r\n" + ex20.ToString());
					return;
				}
				PathGrid.Update(totalMs);
				try
				{
					if (!SuddenDeathActive)
					{
						WeaponSpawnManagerOLD.Update(totalMs);
					}
				}
				catch (Exception ex21)
				{
					m_game.ShowError("Error: GameWorld.Update.WeaponSpawnManager.Update() \r\n" + ex21.ToString());
					return;
				}
			}
			try
			{
				UpdateDialogues(totalMs);
			}
			catch (Exception ex22)
			{
				m_game.ShowError("Error: GameWorld.Update.UpdateDialogues() \r\n" + ex22.ToString());
				return;
			}
		}
		try
		{
			m_box2DsimulationTimeOver += chunkMs;
			float num2 = Constants.BOX2D_SIMULATION_CHUNK_TIME_MS * Math.Min(SlowmotionHandler.SlowmotionModifier, 1f);
			float gibbingImpulseSlowmotionModifierBeforeCorrection = m_gibbingImpulseSlowmotionModifierBeforeCorrection;
			m_gibbingImpulseSlowmotionModifierBeforeCorrection = Math.Min(1f / Math.Min(SlowmotionHandler.SlowmotionModifier, 1f), 4f);
			m_gibbingImpulseSlowmotionModifier = m_gibbingImpulseSlowmotionModifierBeforeCorrection;
			if (m_gibbingImpulseSlowmotionModifierBeforeCorrection != gibbingImpulseSlowmotionModifierBeforeCorrection)
			{
				m_gibbingImpulseSlowmotionModifier *= 0.5f;
			}
			bool flag = m_box2DsimulationTimeOver >= num2;
			while (m_box2DsimulationTimeOver >= num2)
			{
				b2_settings.timeStep = num2 * 0.85f * 0.001f;
				Step(b2_settings);
				m_box2DsimulationTimeOver -= num2;
			}
			RunQueuedScriptOnObjectDamageCallbacks();
			if (flag)
			{
				FireGrid.Update(chunkMs);
				UpdatePlayerFireKamikaze(chunkMs);
			}
			m_drawingBox2DSimulationTimestepOverLastNetTime = NetTime.Now;
			m_drawingBox2DSimulationTimestepOverBase = (double)(m_box2DsimulationTimeOver * 0.85f) * 0.001 * 0.95;
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex24)
		{
			m_game.ShowError("Error: GameWorld.Update.Step() \r\n" + ex24.ToString());
			return;
		}
		if (isFirst)
		{
			try
			{
				for (int num3 = Players.Count - 1; num3 >= 0; num3--)
				{
					Players[num3].Update(totalMs, num);
				}
			}
			catch (GameWorldCorruptedException)
			{
				return;
			}
			catch (Exception ex26)
			{
				m_game.ShowError("Error: GameWorld.Update PlayerUpdates \r\n" + ex26.ToString());
				return;
			}
		}
		try
		{
			UpdateAllProjectiles(chunkMs, isFirst);
		}
		catch (GameWorldCorruptedException)
		{
			return;
		}
		catch (Exception ex28)
		{
			m_game.ShowError("Error: GameWorld.Update.UpdateAllProjectiles() \r\n" + ex28.ToString());
			return;
		}
		if (isLast)
		{
			try
			{
				for (int j = 0; j < Players.Count; j++)
				{
					Player player2 = Players[j];
					player2.PostUpdate(totalMs);
					player2.HandlePlayerKeyHoldingPostUpdateEvent();
				}
				if (ScriptCallbackExists_PlayerKeyInput)
				{
					for (int k = 0; k < Players.Count; k++)
					{
						Player player3 = Players[k];
						if (player3.PlayerKeyInputEvents != null && player3.PlayerKeyInputEvents.Count > 0)
						{
							RunScriptOnPlayerKeyInputCallbacks(player3, player3.PlayerKeyInputEvents);
							player3.PlayerKeyInputEvents.Clear();
						}
					}
				}
			}
			catch (GameWorldCorruptedException)
			{
				return;
			}
			catch (Exception ex30)
			{
				m_game.ShowError("Error: GameWorld.Update PlayerPostUpdates \r\n" + ex30.ToString());
				return;
			}
			if (GameOwner != GameOwnerEnum.Client)
			{
				float num4 = Converter.ConvertWorldToBox2D(BoundsWorldBottom);
				for (Body body = GetActiveWorld.GetBodyList(); body != null; body = body.GetNext())
				{
					if (body.GetType() == Box2D.XNA.BodyType.Dynamic && body.GetUserData() != null && body.GetPosition().Y < num4)
					{
						BodyData bodyData = BodyData.Read(body);
						if (bodyData.IsPlayer)
						{
							((Player)bodyData.Object.InternalData).Remove();
						}
						else
						{
							bodyData.Object.Remove();
						}
					}
				}
				WeaponSpawnManager.Update(totalMs);
			}
			else
			{
				CheckClientHealthChecks();
			}
			UpdateGameTimers(totalMs);
			if (GameOwner != GameOwnerEnum.Server)
			{
				Camera.Update(totalMs, this);
				GameEffects.Update(totalMs);
				if (Weather != null)
				{
					Weather.Update(totalMs, Camera.Zoom);
				}
				try
				{
					if (isLast)
					{
						float num5 = 0f;
						for (int l = 0; l < LocalPlayers.Length; l++)
						{
							Player player4 = LocalPlayers[l];
							if (player4 != null && !player4.IsDisposed && !player4.IsDead)
							{
								num5 = Math.Max(num5, player4.Health.Fullness);
							}
						}
						if (num5 > 0f)
						{
							m_deathSequenceInitialized = false;
							if (GameSFD.GUIMode == ShowGUIMode.HideAll)
							{
								GameSFD.Saturation = 1f;
							}
							else if (num5 < 0.25f)
							{
								float num6 = 1f - num5 / 0.25f;
								GameSFD.Saturation = 1f - num6 * 0.7f;
								m_nextHeartbeatDelay -= totalMs * Math.Max(num6, 0.6f);
								if (m_nextHeartbeatDelay <= 0f)
								{
									m_nextHeartbeatDelay = 400f;
									SoundHandler.PlaySound("Heartbeat", 1f, this);
								}
							}
							else
							{
								GameSFD.Saturation = 1f;
							}
						}
						else if (CheckAllLocalPlayersDead())
						{
							if (!m_deathSequenceInitialized)
							{
								if (ObjectWorldData.DeathSequenceEnabled && GameSFD.GUIMode != ShowGUIMode.HideAll)
								{
									SoundHandler.PlayGlobalSound("Death1");
								}
								m_deathSequenceInitialized = true;
								m_deathSequenceFadeInTimer = 0f;
							}
							if (m_deathSequenceFadeInTimer < 5000f)
							{
								m_deathSequenceFadeInTimer += totalMs;
							}
							if (GameSFD.GUIMode == ShowGUIMode.HideAll)
							{
								GameSFD.Saturation = 1f;
							}
							else if (m_deathSequenceFadeInTimer < 5000f)
							{
								GameSFD.Saturation = 0f;
							}
							else if (GameSFD.Saturation < 1f)
							{
								GameSFD.Saturation = Math.Min(1f, GameSFD.Saturation + 0.00025f * totalMs);
							}
							else
							{
								GameSFD.Saturation = 1f;
							}
						}
						else
						{
							GameSFD.Saturation = 1f;
						}
					}
				}
				catch (Exception ex31)
				{
					m_game.ShowError("Error: Failed to progress low health effects and sound() \r\n" + ex31.ToString());
					return;
				}
			}
		}
		if (!m_firstUpdateIsRun)
		{
			try
			{
				AfterStartup();
			}
			catch (GameWorldCorruptedException)
			{
				return;
			}
			catch (Exception ex33)
			{
				m_game.ShowError("Error: GameWorld.Update.AfterStartup\r\n" + ex33.ToString());
				return;
			}
		}
		m_firstUpdateIsRun = true;
	}

	public void PausedFrame()
	{
		m_drawingBox2DSimulationTimestepOverLastNetTime = NetTime.Now;
	}

	public void UpdateEditorContent()
	{
		ObjectPathDebugTarget.UpdatePathDebugTargets(this);
	}

	public void UpdatePlayerFireKamikaze(float chunkMs)
	{
		HashSet<Player> hashSet = null;
		foreach (Player player in Players)
		{
			if (!player.BurningInferno)
			{
				continue;
			}
			foreach (Player player2 in Players)
			{
				if (player2.BurningInferno || !(Math.Abs(player2.PreBox2DPosition.X - player.PreBox2DPosition.X) < 0.79999995f) || (hashSet != null && hashSet.Contains(player2)))
				{
					continue;
				}
				player.GetAABBWhole(out var aabb);
				player2.GetAABBWhole(out var aabb2);
				if (AABB.TestOverlap(ref aabb, ref aabb2))
				{
					if (hashSet == null)
					{
						hashSet = new HashSet<Player>();
					}
					hashSet.Add(player2);
					player2.ObjectData.TakeFireDamage(0f, 0.05f * chunkMs);
				}
			}
		}
	}

	public void Step(Box2DSettings settings)
	{
		float num = (m_lastBox2DTimeStep = settings.timeStep);
		if (!b2_flagsSet)
		{
			uint num2 = 0u;
			num2 = 0 + settings.drawShapes;
			num2 += settings.drawJoints * 2;
			num2 += settings.drawAABBs * 4;
			num2 += settings.drawPairs * 8;
			num2 += settings.drawCOMs * 16;
			b2_debugDraw.Flags = (DebugDrawFlags)num2;
			b2_flagsSet = true;
		}
		b2_world_active.WarmStarting = settings.enableWarmStarting != 0;
		b2_world_active.ContinuousPhysics = settings.enableContinuous != 0;
		for (int i = 0; i < Players.Count; i++)
		{
			Players[i].StepPlayerPreBox2DAction(num);
		}
		UpdateObjectBeforeBox2DStep(num);
		MeleeKickUpdate();
		b2_world_active.Step(num, settings.velocityIterations, settings.positionIterations);
		HandleImpulsePoints();
		CheckPlayerPlayerCollisions();
		UpdateMissileObjectAfterBox2DStep(num);
		for (int j = 0; j < Players.Count; j++)
		{
			Players[j].StepPlayerPostBox2DAction();
		}
	}

	public List<Player> GetOverlappingPlayers(ref AABB aabb)
	{
		List<Player> list = new List<Player>();
		for (int i = 0; i < Players.Count; i++)
		{
			Player player = Players[i];
			if (!player.IsRemoved)
			{
				player.GetAABBWhole(out var aabb2);
				if (AABB.TestOverlap(ref aabb2, ref aabb))
				{
					list.Add(player);
				}
			}
		}
		return list;
	}

	public void CheckPlayerPlayerCollisions(Player sourcePlayer = null, bool forceTriggering = false)
	{
		List<Player> list = null;
		if (sourcePlayer == null)
		{
			for (int i = 0; i < Players.Count; i++)
			{
				Player player = Players[i];
				if (player.IsRemoved)
				{
					continue;
				}
				if (player.InFreeAir && !player.PerformingStunt && !player.IsCaughtByPlayer && !player.IsGrabbedByPlayer && player.RocketRideProjectileWorldID <= 0)
				{
					Microsoft.Xna.Framework.Vector2 preBox2DLinearVelocity = player.PreBox2DLinearVelocity;
					Microsoft.Xna.Framework.Vector2 playerCollisionTriggerVelocity = player.GetPlayerCollisionTriggerVelocity();
					if ((Math.Abs(preBox2DLinearVelocity.X) > playerCollisionTriggerVelocity.X) | (Math.Abs(preBox2DLinearVelocity.Y) > playerCollisionTriggerVelocity.Y))
					{
						if (list == null)
						{
							list = new List<Player>();
						}
						list.Add(player);
					}
				}
				player.UpdateCollisionPlayerPlayerOverlappings();
			}
		}
		else
		{
			list = new List<Player>();
			list.Add(sourcePlayer);
		}
		if (list == null)
		{
			return;
		}
		HashSet<Player> hashSet = new HashSet<Player>();
		foreach (Player item in list)
		{
			if (!hashSet.Add(item))
			{
				continue;
			}
			for (int j = 0; j < Players.Count; j++)
			{
				Player player2 = Players[j];
				if (player2.IsRemoved || player2 == item || item.CheckCollisionPlayerPlayerOverlapping(player2) || player2.CaughtByPlayer == item || player2.RocketRideProjectileWorldID > 0 || Math.Abs(item.PreBox2DPosition.X - player2.PreBox2DPosition.X) > 0.32f || Math.Abs(item.PreBox2DPosition.Y - player2.PreBox2DPosition.Y) > 0.64f)
				{
					continue;
				}
				Microsoft.Xna.Framework.Vector2 playerCollisionTriggerVelocity2 = item.GetPlayerCollisionTriggerVelocity();
				if (forceTriggering)
				{
					playerCollisionTriggerVelocity2.X = 10f;
					playerCollisionTriggerVelocity2.Y = 10f;
				}
				Microsoft.Xna.Framework.Vector2 vector = item.PreBox2DLinearVelocity * item.VelocityNetworkFactor;
				Microsoft.Xna.Framework.Vector2 vector2 = player2.PreBox2DLinearVelocity * player2.VelocityNetworkFactor;
				Microsoft.Xna.Framework.Vector2 vector3 = (vector - vector2).Sanitize();
				if (!((Math.Abs(vector3.X) > playerCollisionTriggerVelocity2.X) | (Math.Abs(vector3.Y) > playerCollisionTriggerVelocity2.Y)))
				{
					continue;
				}
				item.GetAABBWhole(out var aabb);
				player2.GetAABBWhole(out var aabb2);
				if (!aabb.Overlap(ref aabb2))
				{
					continue;
				}
				Microsoft.Xna.Framework.Vector2 vector4 = player2.PreBox2DPosition - item.PreBox2DPosition;
				Microsoft.Xna.Framework.Vector2 vector5 = item.PreBox2DPosition + vector4 * 0.5f;
				AABB aabb3 = default(AABB);
				aabb3.lowerBound = vector5;
				aabb3.upperBound = vector5;
				aabb3.Grow(0.32f);
				List<Player> overlappingPlayers = GetOverlappingPlayers(ref aabb3);
				if (!overlappingPlayers.Contains(player2))
				{
					overlappingPlayers.Add(player2);
				}
				overlappingPlayers.Remove(item);
				hashSet.AddRange(overlappingPlayers);
				Microsoft.Xna.Framework.Vector2 vector6 = player2.PreBox2DPosition - item.PreBox2DPosition;
				vector6.Normalize();
				if (vector6.IsValid())
				{
					Microsoft.Xna.Framework.Vector2 value = Microsoft.Xna.Framework.Vector2.Normalize(vector3);
					for (int num = overlappingPlayers.Count - 1; num >= 0; num--)
					{
						if (Microsoft.Xna.Framework.Vector2.Dot(vector6, value) < -0.25f && vector4.CalcSafeLength() > 0.16f)
						{
							overlappingPlayers.RemoveAt(num);
						}
					}
				}
				if (overlappingPlayers.Count == 0)
				{
					continue;
				}
				if (overlappingPlayers.Count > 1)
				{
					vector2 = Microsoft.Xna.Framework.Vector2.Zero;
					foreach (Player item2 in overlappingPlayers)
					{
						vector2 += item2.PreBox2DLinearVelocity * item2.VelocityNetworkFactor;
					}
					vector2 /= (float)overlappingPlayers.Count;
					vector3 = (vector - vector2).Sanitize();
				}
				item.AirControlBaseVelocity.X = 0f;
				item.AirControlBaseVelocity.Y = 0f;
				foreach (Player item3 in overlappingPlayers)
				{
					item3.AirControlBaseVelocity.X = 0f;
					item3.AirControlBaseVelocity.Y = 0f;
					item.SetCollisionPlayerPlayerOverlapping(item3, 500f);
				}
				Microsoft.Xna.Framework.Vector2 vector7 = (vector2 * 0.75f + vector * 0.25f) / overlappingPlayers.Count;
				Microsoft.Xna.Framework.Vector2 otherPlrLinearVelocityAfter = (vector * 0.75f + vector2 * 0.25f) / overlappingPlayers.Count;
				float playerDamage = Player.CalculateFallDamage(vector3.CalcSafeLength()) * 0.25f;
				item.SetCollisionPlayerPlayerOverlapping(player2, 500f);
				if (!item.HasMeleeStunImmunity)
				{
					if (GameOwner == GameOwnerEnum.Client)
					{
						item.SimulateFallWithSpeed(vector7);
					}
					else
					{
						item.FallWithSpeed(vector7);
					}
				}
				item.RegisterPlayerImpact(player2, playerDamage, otherPlrLinearVelocityAfter, vector7, item.WorldBody.GetPosition());
				foreach (Player item4 in overlappingPlayers)
				{
					vector2 = item4.PreBox2DLinearVelocity * item4.VelocityNetworkFactor;
					otherPlrLinearVelocityAfter = (vector * 0.75f + vector2 * 0.25f) / overlappingPlayers.Count;
					if (!item4.HasMeleeStunImmunity)
					{
						if (GameOwner == GameOwnerEnum.Client)
						{
							item4.SimulateFallWithSpeed(otherPlrLinearVelocityAfter);
						}
						else
						{
							item4.FallWithSpeed(otherPlrLinearVelocityAfter);
						}
					}
					item4.RegisterPlayerImpact(item, playerDamage, vector7, otherPlrLinearVelocityAfter, item4.WorldBody.GetPosition());
				}
			}
		}
		list.Clear();
		list = null;
	}

	public Player GetPlayerByUserIdentifier(int userIdentifier)
	{
		if (userIdentifier == 0)
		{
			return null;
		}
		int num = 0;
		while (true)
		{
			if (num < Players.Count)
			{
				if (Players[num].UserIdentifier == userIdentifier)
				{
					break;
				}
				num++;
				continue;
			}
			return null;
		}
		return Players[num];
	}

	public Team GetPlayerTeamByUserIdentifier(int userIdentifier)
	{
		return GetPlayerByUserIdentifier(userIdentifier)?.CurrentTeam ?? Team.Independent;
	}

	public Player GetPlayer(int playerID)
	{
		Player value = null;
		if (PlayersLookup.TryGetValue(playerID, out value))
		{
			return value;
		}
		return null;
	}

	public List<ObjectData> GetObjectDataByCustomID(string customID)
	{
		List<ObjectData> result = new List<ObjectData>();
		if (!CustomIDTableLookup.ContainsKey(customID))
		{
			return result;
		}
		int value = 0;
		if (CustomIDTableLookup.TryGetValue(customID, out value))
		{
			return GetObjectDataByCustomID(value);
		}
		return result;
	}

	public ObjectData GetSingleObjectDataByCustomID(string customID)
	{
		if (!CustomIDTableLookup.ContainsKey(customID))
		{
			return null;
		}
		int value = 0;
		if (CustomIDTableLookup.TryGetValue(customID, out value))
		{
			List<ObjectData> objectDataByCustomID = GetObjectDataByCustomID(value);
			if (objectDataByCustomID.Count > 0)
			{
				return objectDataByCustomID[0];
			}
		}
		return null;
	}

	public List<ObjectData> GetObjectDataByScriptType<T>() where T : IObject
	{
		List<ObjectData> list = new List<ObjectData>();
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			if (staticObject.Value.ScriptBridge is T)
			{
				list.Add(staticObject.Value);
			}
		}
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			if (dynamicObject.Value.ScriptBridge is T)
			{
				list.Add(dynamicObject.Value);
			}
		}
		foreach (Player player in Players)
		{
			if (player.ObjectData.ScriptBridge is T)
			{
				list.Add(player.ObjectData);
			}
		}
		return list;
	}

	public List<ObjectData> GetObjectDataByCustomID(int customID)
	{
		List<ObjectData> list = new List<ObjectData>();
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			if (staticObject.Value.CustomID == customID)
			{
				list.Add(staticObject.Value);
			}
		}
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			if (dynamicObject.Value.CustomID == customID)
			{
				list.Add(dynamicObject.Value);
			}
		}
		foreach (Player player in Players)
		{
			if (player.ObjectData.CustomID == customID)
			{
				list.Add(player.ObjectData);
			}
		}
		return list;
	}

	public List<ObjectData> GetObjectDataByMapObjectID(string mapObjectID)
	{
		List<ObjectData> list = new List<ObjectData>();
		if (string.IsNullOrEmpty(mapObjectID))
		{
			return list;
		}
		mapObjectID = mapObjectID.ToUpperInvariant();
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			if (staticObject.Value.MapObjectID == mapObjectID)
			{
				list.Add(staticObject.Value);
			}
		}
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			if (dynamicObject.Value.MapObjectID == mapObjectID)
			{
				list.Add(dynamicObject.Value);
			}
		}
		return list;
	}

	public T GetObjectDataByIDAndType<T>(int ID) where T : ObjectData
	{
		if (ID == 0)
		{
			return null;
		}
		ObjectData objectDataByID = GetObjectDataByID(ID);
		if (objectDataByID != null && objectDataByID is T)
		{
			return (T)objectDataByID;
		}
		return null;
	}

	public List<T> GetObjectDataByType<T>() where T : ObjectData
	{
		List<T> list = new List<T>();
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			if (staticObject.Value is T)
			{
				list.Add((T)staticObject.Value);
			}
		}
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			if (dynamicObject.Value is T)
			{
				list.Add((T)dynamicObject.Value);
			}
		}
		return list;
	}

	public List<ObjectData> GetObjectDataByMapObjectID(string[] mapObjectIDs)
	{
		List<ObjectData> list = new List<ObjectData>();
		if (mapObjectIDs != null && mapObjectIDs.Length != 0)
		{
			foreach (string mapObjectID in mapObjectIDs)
			{
				list.AddRange(GetObjectDataByMapObjectID(mapObjectID));
			}
			return list;
		}
		return list;
	}

	public List<ObjectData> GetObjectDataByArea(AABB worldAABB, bool firstOnly, PhysicsLayer physicsLayer)
	{
		worldAABB.lowerBound = Converter.WorldToBox2D(worldAABB.lowerBound);
		worldAABB.upperBound = Converter.WorldToBox2D(worldAABB.upperBound);
		HashSet<ObjectData> objects = new HashSet<ObjectData>();
		if ((physicsLayer & PhysicsLayer.Active) == PhysicsLayer.Active)
		{
			GetActiveWorld.QueryAABB(delegate(Fixture fixture)
			{
				if (fixture != null && fixture.GetUserData() != null)
				{
					ObjectData item = ObjectData.Read(fixture);
					objects.Add(item);
					if (firstOnly)
					{
						return false;
					}
				}
				return true;
			}, ref worldAABB);
		}
		if ((physicsLayer & PhysicsLayer.Background) == PhysicsLayer.Background && (!firstOnly || objects.Count == 0))
		{
			GetBackgroundWorld.QueryAABB(delegate(Fixture fixture)
			{
				if (fixture != null && fixture.GetUserData() != null)
				{
					ObjectData item = ObjectData.Read(fixture);
					objects.Add(item);
					if (firstOnly)
					{
						return false;
					}
				}
				return true;
			}, ref worldAABB);
		}
		List<ObjectData> list = new List<ObjectData>();
		list.AddRange(objects);
		return list;
	}

	public List<ObjectData> GetObjectDataByID(int[] IDs)
	{
		List<ObjectData> list = new List<ObjectData>();
		foreach (int iD in IDs)
		{
			ObjectData objectDataByID = GetObjectDataByID(iD);
			if (objectDataByID != null)
			{
				list.Add(objectDataByID);
			}
		}
		return list;
	}

	public ObjectData GetObjectDataByID(int ID)
	{
		if (DynamicObjects.TryGetValue(ID, out var value))
		{
			return value;
		}
		if (StaticObjects.TryGetValue(ID, out value))
		{
			return value;
		}
		return GetPlayer(ID)?.ObjectData;
	}

	public List<ObjectData> GetObjectDataByGroupID(ushort groupID)
	{
		List<ObjectData> list = new List<ObjectData>();
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			if (staticObject.Value.GroupID == groupID)
			{
				list.Add(staticObject.Value);
			}
		}
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			if (dynamicObject.Value.GroupID == groupID)
			{
				list.Add(dynamicObject.Value);
			}
		}
		return list;
	}

	public IEnumerable<ObjectData> AllObjectData()
	{
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			yield return dynamicObject.Value;
		}
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			yield return staticObject.Value;
		}
	}

	public BodyData GetBodyDataByID(int bodyID)
	{
		if (DynamicBodies.ContainsKey(bodyID))
		{
			return DynamicBodies[bodyID];
		}
		if (StaticBodies.ContainsKey(bodyID))
		{
			return StaticBodies[bodyID];
		}
		return null;
	}

	public void UpdateGameOver(float ms)
	{
		try
		{
			if (!GameOverData.IsOver)
			{
				m_gameOverNextCheck -= ms;
				if (m_gameOverNextCheck <= 0f)
				{
					UpdateGameOverData(forceCheck: false);
					m_gameOverNextCheck = 100f;
				}
			}
			else if (GameOverData.IsOver)
			{
				if (!m_gameOverSignalSent)
				{
					if (m_gameOverDelayTime == 0f)
					{
						RunOnGameOverTriggers();
						m_gameOverDelayTime = 3000f;
					}
					if (m_gameOverDelayTime > 0f)
					{
						m_gameOverDelayTime -= ms;
						if (m_gameOverDelayTime <= 0f)
						{
							UpdateGameOverData();
							if (GameOverData.GameOverGibOnTimesUp && GameOverData.GameOverType == GameOverType.TimesUp)
							{
								foreach (Player player in Players)
								{
									if (!player.IsDead)
									{
										player.SetFunctionToRunNextUpdate(Player.RunNextUpdate.Explode);
									}
								}
							}
							if (!GameOverData.GameOverScoreUpdated)
							{
								UpdateGameScore();
								GameOverData.GameOverScoreUpdated = true;
							}
							GameOverData.GameOverGibOnTimesUp = false;
							if (GameOwner == GameOwnerEnum.Server)
							{
								GameOverData.GameOverMaxVotes = m_game.Server.ConnectionsCount;
								GameOverData.GameOverVotes = 0;
								GameOverData.GameOverTimeLeft = GameInfo.MapAutoRestartTime / 1000;
								SyncGameOverChanges();
								m_game.Server.SetClientsReady(value: false);
								SyncGameOverChanges(NetMessage.Signal.Type.GameOverUpdateSignal);
							}
							else if (GameOwner == GameOwnerEnum.Local)
							{
								GameOverData.GameOverMaxVotes = 1;
								GameOverData.GameOverVotes = 0;
								GameOverData.GameOverTimeLeft = GameInfo.MapAutoRestartTime / 1000;
							}
							GameOverData.GameOverTimeLeft = GameInfo.MapAutoRestartTime / 1000;
							m_gameOverSignalSent = true;
							m_gameOverDelayTime = 0f;
							m_gameOverAutomaticallyRestartTimer = GameInfo.MapAutoRestartTime;
							m_gameOverLastAutoSecondSend = -1f;
						}
					}
				}
				else if (m_gameOverAutomaticallyRestartTimer > 0f)
				{
					m_gameOverAutomaticallyRestartTimer -= ms;
					int num = (int)Math.Floor(m_gameOverAutomaticallyRestartTimer / 1000f);
					GameOverData.GameOverTimeLeft = num;
					if ((float)num != m_gameOverLastAutoSecondSend)
					{
						m_gameOverLastAutoSecondSend = num;
						if (GameOwner == GameOwnerEnum.Server)
						{
							m_gameOverVoteDoneMajorityIsReady |= m_game.Server.IsMajorityClientsReady(out var readyCount, out var _, out var maxCount);
							GameOverData.GameOverMaxVotes = maxCount;
							GameOverData.GameOverVotes = readyCount;
							GameOverData.GameOverTimeLeft = num;
							SyncGameOverChanges();
						}
						else if (GameOwner == GameOwnerEnum.Local)
						{
							GameOverData.GameOverTimeLeft = num;
						}
					}
					if (GameOverData.GameOverVotes >= GameOverData.GameOverMaxVotes || m_gameOverVoteDoneMajorityIsReady)
					{
						m_gameOverVoteDoneDelayTimer += ms;
						if (!GameOverData.GameOverContinueVotesDone && m_gameOverVoteDoneDelayTimer > 1000f)
						{
							GameOverData.GameOverContinueVotesDone = true;
							StorePlayerStats();
						}
					}
					if (m_gameOverAutomaticallyRestartTimer <= 0f && !GameOverData.GameOverTimeDone)
					{
						GameOverData.GameOverTimeDone = true;
						StorePlayerStats();
					}
				}
			}
			if (!m_restartInstant)
			{
				return;
			}
			m_restartInstant = false;
			if (!GameOverData.GameOverTimeDone)
			{
				GameOverData.GameOverTimeDone = true;
				StorePlayerStats();
				if (!GameOverData.GameOverScoreUpdated)
				{
					UpdateGameScore();
					GameOverData.GameOverScoreUpdated = true;
				}
			}
		}
		catch (Exception exception)
		{
			Program.ShowError(exception, "GameWorld.UpdateGameOver() failed");
		}
	}

	public void SyncGameOverChanges(NetMessage.Signal.Type gameOverSignalType = NetMessage.Signal.Type.GameOverSignal)
	{
		if (GameOwner == GameOwnerEnum.Server)
		{
			GameOverResultUpdate obj = new GameOverResultUpdate(GameOverData);
			m_game.Server.SendMessage(MessageType.Signal, new NetMessage.Signal.Data(gameOverSignalType, obj));
		}
	}

	public GameOverResultData UpdateGameOverData(bool forceCheck = true)
	{
		if ((GameOverData.IsOver && !forceCheck) || GameOverData.Reason == GameOverReason.Custom)
		{
			return GameOverData;
		}
		switch (MapType)
		{
		default:
			return GameOverData;
		case MapType.Custom:
		case MapType.Campaign:
			if (GameOverData.Reason == GameOverReason.TimesUp)
			{
				GameOverData.GameOverType = GameOverType.TimesUp;
			}
			return GameOverData;
		case MapType.Survival:
			if (GameOverData.IsOver && GameOverData.GameOverType != GameOverType.SurvivalVictory && GameOverData.GameOverType != GameOverType.SurvivalLoss)
			{
				if (GameOverData.Reason == GameOverReason.TimesUp)
				{
					GameInfo.MapSessionData.SurvivalWaveFinished = false;
					GameOverData.GameOverType = GameOverType.SurvivalLoss;
				}
				else
				{
					GameInfo.MapSessionData.SurvivalWaveFinished = GameOverData.WinningUserIdentifiers.Any((int x) => x != 0);
					GameOverData.GameOverType = (GameInfo.MapSessionData.SurvivalWaveFinished ? GameOverType.SurvivalVictory : GameOverType.SurvivalLoss);
				}
				if (GameOverData.GameOverType == GameOverType.SurvivalVictory)
				{
					GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data("game.survival.wavecomplete", Microsoft.Xna.Framework.Color.ForestGreen, GameInfo.MapSessionData.SurvivalWave.ToString()));
				}
				else if (GameOverData.GameOverType == GameOverType.SurvivalLoss)
				{
					GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data("game.survival.wavefailed", Microsoft.Xna.Framework.Color.Red, GameInfo.MapSessionData.SurvivalWave.ToString()));
					if (GameInfo.MapSessionData.SurvivalExtraLives > 0)
					{
						if (GameInfo.PlayingGameUserCount > 0)
						{
							GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data((GameInfo.MapSessionData.SurvivalExtraLives == 1) ? "game.survival.liferemains.one" : "game.survival.liferemains.many", Microsoft.Xna.Framework.Color.Yellow, GameInfo.MapSessionData.SurvivalExtraLives.ToString()));
						}
						else
						{
							GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data("game.survival.noplayingusers", Microsoft.Xna.Framework.Color.Red, ""));
						}
					}
					else
					{
						GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data("statusText.survivalfinalscore", Microsoft.Xna.Framework.Color.Yellow, GameInfo.TotalScore.ToString()));
					}
				}
			}
			return GameOverData;
		case MapType.Versus:
		case MapType.Challenge:
		{
			if (GameOverData.Reason == GameOverReason.TimesUp)
			{
				GameOverData.GameOverType = GameOverType.TimesUp;
			}
			if (!AutoVictoryConditionEnabled && !GameOverData.IsOver)
			{
				return GameOverData;
			}
			if (MapType == MapType.Challenge && !AutoVictoryConditionEnabled)
			{
				return GameOverData;
			}
			int num = Players.Count((Player x) => !x.IsDead);
			if (num == 0)
			{
				if (GameInfo.GameUserCount == 0)
				{
					return GameOverData;
				}
				GameOverData.WinningUserIdentifiers.Clear();
				GameOverData.IsOver = true;
				GameOverData.Team = Team.Independent;
				GameOverData.GameOverType = GameOverType.Nobody;
				GameOverData.Text = "";
				if (GameOverData.Reason == GameOverReason.TimesUp)
				{
					GameOverData.GameOverType = GameOverType.TimesUp;
				}
				return GameOverData;
			}
			if (PlayingUsersVersusMode < PlayingUserMode.Multi)
			{
				int val = Math.Min(2, num);
				PlayingUsersVersusMode = (PlayingUserMode)Math.Max(val, (int)PlayingUsersVersusMode);
			}
			bool flag = true;
			for (int num2 = 0; num2 < Players.Count; num2++)
			{
				Player player = Players[num2];
				if (!player.IsDead && player.RocketRideProjectileWorldID == 0)
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				GameOverData.WinningUserIdentifiers.Clear();
				GameOverData.IsOver = true;
				GameOverData.Team = Team.Independent;
				GameOverData.GameOverType = GameOverType.Nobody;
				GameOverData.Text = "";
				if (GameOverData.Reason == GameOverReason.TimesUp)
				{
					GameOverData.GameOverType = GameOverType.TimesUp;
				}
				return GameOverData;
			}
			Player survivingPlayer = null;
			int num3 = 0;
			Team team = Team.Independent;
			int num4 = 0;
			while (true)
			{
				if (num4 < Players.Count)
				{
					Player player2 = Players[num4];
					if (!player2.IsDead)
					{
						survivingPlayer = player2;
						num3++;
						if (num3 > 1 && (team != player2.CurrentTeam || (team == Team.Independent && player2.CurrentTeam == Team.Independent)))
						{
							break;
						}
						team = player2.CurrentTeam;
					}
					num4++;
					continue;
				}
				if (num3 == 0)
				{
					GameOverData.WinningUserIdentifiers.Clear();
					GameOverData.IsOver = true;
					GameOverData.Team = Team.Independent;
					GameOverData.GameOverType = GameOverType.Nobody;
					GameOverData.Text = "";
					if (GameOverData.Reason == GameOverReason.TimesUp)
					{
						GameOverData.GameOverType = GameOverType.TimesUp;
					}
					return GameOverData;
				}
				if (PlayingUsersVersusMode == PlayingUserMode.Single)
				{
					GameOverData.WinningUserIdentifiers.Clear();
					GameOverData.Team = team;
					if (survivingPlayer != null)
					{
						GameOverData.GameOverType = GameOverType.PlayerWins;
						GameOverData.Text = survivingPlayer.Name;
						GameOverData.WinningUserIdentifiers.Add(survivingPlayer.UserIdentifier);
					}
					else
					{
						GameOverData.GameOverType = GameOverType.Nobody;
						GameOverData.Text = "";
					}
					if (GameOverData.Reason == GameOverReason.TimesUp)
					{
						GameOverData.GameOverType = GameOverType.TimesUp;
					}
					if (MapType == MapType.Versus)
					{
						if (GameInfo.MapInfo.GetActualTotalPlayers() == 1)
						{
							if (survivingPlayer == null)
							{
								GameOverData.IsOver = true;
							}
						}
						else if ((from x in GameInfo.GetGameUsers()
							where survivingPlayer == null || x.UserIdentifier != survivingPlayer.UserIdentifier
							select x).Any() && (from x in GameInfo.GetGameUsers()
							where survivingPlayer == null || x.UserIdentifier != survivingPlayer.UserIdentifier
							select x).All((GameUser x) => x.SpectatingWhileWaitingToPlay))
						{
							GameOverData.IsOver = true;
						}
					}
					return GameOverData;
				}
				List<GameUser> gameOverWinningGameUsers = GetGameOverWinningGameUsers();
				GameOverData.WinningUserIdentifiers.Clear();
				GameOverData.IsOver = true;
				GameOverData.Team = Team.Independent;
				GameOverData.Text = "";
				if (gameOverWinningGameUsers.Count > 0)
				{
					GameOverData.Reason = GameOverReason.Default;
					Team team2 = gameOverWinningGameUsers[0].GameSlotTeam;
					if (team2 == Team.Independent)
					{
						team2 = GetPlayerTeamByUserIdentifier(gameOverWinningGameUsers[0].UserIdentifier);
					}
					if (gameOverWinningGameUsers.Count == 1 && team2 == Team.Independent)
					{
						GameOverData.GameOverType = GameOverType.PlayerWins;
						GameOverData.Team = team2;
						GameOverData.Text = gameOverWinningGameUsers[0].GetProfileName();
					}
					else
					{
						GameOverData.GameOverType = GameOverType.TeamWins;
						GameOverData.Team = team2;
						GameOverData.Text = "";
					}
					GameOverData.WinningUserIdentifiers.AddRange(gameOverWinningGameUsers.Select((GameUser x) => x.UserIdentifier));
				}
				else
				{
					GameOverData.GameOverType = GameOverType.Nobody;
					GameOverData.Team = Team.Independent;
					GameOverData.Text = "";
					if (GameOverData.Reason == GameOverReason.TimesUp)
					{
						GameOverData.GameOverType = GameOverType.TimesUp;
					}
				}
				return GameOverData;
			}
			if (GameOverData.Reason == GameOverReason.TimesUp)
			{
				GameOverData.GameOverType = GameOverType.TimesUp;
			}
			return GameOverData;
		}
		}
	}

	public List<GameUser> GetGameOverWinningGameUsers()
	{
		if (MapType == MapType.Survival)
		{
			if (!GameInfo.MapSessionData.SurvivalWaveFinished)
			{
				return new List<GameUser>();
			}
			return (from x in GameInfo.GetGameUsers()
				where x.CanWin
				select x).ToList();
		}
		if (MapType == MapType.Campaign)
		{
			if (GameInfo.CampaignMapPartChangeType == GameInfo.CampaignMapPartChangeTypeEnum.RestartCurrentPart)
			{
				return new List<GameUser>();
			}
			return (from x in GameInfo.GetGameUsers()
				where x.CanWin
				select x).ToList();
		}
		Team team = Team.Independent;
		GameUser gameUser = null;
		List<GameUser> list = new List<GameUser>();
		int num = 0;
		int num2 = 0;
		while (true)
		{
			if (num2 < Players.Count)
			{
				Player player = Players[num2];
				if (!player.IsDead && player.RocketRideProjectileWorldID == 0)
				{
					num++;
					if (num > 1 && (team != player.CurrentTeam || (team == Team.Independent && player.CurrentTeam == Team.Independent)))
					{
						break;
					}
					team = player.CurrentTeam;
					gameUser = player.GetGameUser();
				}
				num2++;
				continue;
			}
			if (num == 0)
			{
				return list;
			}
			foreach (GameUser gameUser2 in GameInfo.GetGameUsers())
			{
				if (!gameUser2.CanWin)
				{
					continue;
				}
				if (team == Team.Independent)
				{
					if (gameUser != null && gameUser.UserIdentifier == gameUser2.UserIdentifier && gameUser.LocalUserIndex == gameUser2.LocalUserIndex)
					{
						list.Add(gameUser2);
					}
				}
				else if (gameUser2.GameSlotTeam == team)
				{
					list.Add(gameUser2);
				}
			}
			return list;
		}
		return list;
	}

	public void UpdateGameScore()
	{
		List<GameUser> list = new List<GameUser>();
		if (AutoScoreConditionEnabled)
		{
			list.AddRange(GetGameOverWinningGameUsers());
		}
		foreach (GameUser gameUser in GameInfo.GetGameUsers())
		{
			if (gameUser.IncreaseScore && !list.Contains(gameUser))
			{
				list.Add(gameUser);
			}
			gameUser.IncreaseScore = false;
		}
		foreach (GameUser gameUser2 in GameInfo.GetGameUsers())
		{
			if (!gameUser2.SpectatingWhileWaitingToPlay)
			{
				bool num = list.Contains(gameUser2);
				gameUser2.Score.TotalGames++;
				if (num)
				{
					gameUser2.Score.TotalWins++;
				}
				else
				{
					gameUser2.Score.TotalLosses++;
				}
			}
		}
		if (GameOwner == GameOwnerEnum.Server)
		{
			m_game.Server.SyncGameScoreInfo();
		}
	}

	public void StorePlayerStats()
	{
		foreach (GameUser gameUser in GameInfo.GetGameUsers())
		{
			GameInfo.StorePlayerStats(gameUser);
		}
	}

	public void SetGameOver(GameOverReason gameOverReason = GameOverReason.Default)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: Only the server / local can call GameWorld.SetGameOver()");
		}
		if (!GameOverData.IsOver)
		{
			GameOverData.IsOver = true;
			GameOverData.Reason = gameOverReason;
			GameOverData.WinningUserIdentifiers = GetAliveGameUserIdentifiers();
			UpdateGameOverData();
			GameOverData.GameOverVotes = 0;
			GameOverData.GameOverTimeLeft = GameInfo.MapAutoRestartTime / 1000;
		}
	}

	public void FinalizeProperties()
	{
		b2_settings.timeStep = 0f;
		Step(b2_settings);
		foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
		{
			dynamicObject.Value.FinalizeProperties();
		}
		foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
		{
			staticObject.Value.FinalizeProperties();
		}
	}

	public void RunGameCommand(string command, bool startup = false, bool forceRunCommand = false)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: GameWorld.RunGameCommand() is SERVER/LOCAL ONLY");
		}
		if (string.IsNullOrEmpty(command) || !command.StartsWith("/"))
		{
			return;
		}
		bool flag = false;
		if (!forceRunCommand)
		{
			if (CurrentActiveScriptIsExtension)
			{
				flag = command.ToLowerInvariant() != "/r" && !command.ToLowerInvariant().StartsWith("/r ");
			}
			else
			{
				string[] obj = new string[23]
				{
					"/SETSTARTHEALTH", "/SETSTARTLIFE", "/STARTHEALTH", "/STARTLIFE", "/MSG", "/MESSAGE", "/STARTITEMS", "/STARTITEM", "/SETSTARTUPITEM", "/SETSTARTUPITEMS",
					"/SETSTARTITEM", "/SETSTARTITEMS", "/INFINITE_ENERGY", "/IE", "/INFINITE_AMMO", "/IA", "/INFINITE_LIFE", "/IL", "/INFINITE_HEALTH", "/IH",
					"/SETTIME", "/REMOVE", "/GIVE"
				};
				string text = command.ToUpperInvariant();
				string[] array = obj;
				foreach (string value in array)
				{
					if (flag = text.StartsWith(value))
					{
						break;
					}
				}
			}
		}
		else
		{
			flag = true;
		}
		if (flag)
		{
			HandleCommandArgs handleCommandArgs = new HandleCommandArgs();
			handleCommandArgs.Command = command;
			handleCommandArgs.UserIdentifier = 1;
			handleCommandArgs.Origin = (startup ? HandleCommandOrigin.Startup : HandleCommandOrigin.Server);
			if (!GameInfo.HandleCommand(handleCommandArgs) && Program.IsGame && m_game.CurrentState == State.EditorTestRun)
			{
				MessageStack.Show($"Command '{command}' failed or not valid.", MessageStackType.Warning);
			}
		}
		else if (Program.IsGame && m_game.CurrentState == State.EditorTestRun)
		{
			MessageStack.Show($"Command '{command}' is not a valid game map command", MessageStackType.Error);
		}
	}

	public void PrepareAfterLoading(IChallenge challenge = null, bool spawnUsers = true, bool startScripts = true)
	{
		if (GameOwner != GameOwnerEnum.Client)
		{
			GameInfo.AfterMapLoad(MapType);
		}
		FinalizeProperties();
		m_newlyCreatedObjects.Clear();
		if (GameOwner != GameOwnerEnum.Client)
		{
			PrepareLocalStorage("");
			if (spawnUsers)
			{
				GameInfo.SpawnUsers(challenge);
			}
			m_newlyCreatedPlayers.Clear();
			GameInfo.MapSessionData.SurvivalWaveFinished = false;
			RunStartup();
			if (startScripts)
			{
				GameInfo.SyncScripts();
			}
			challenge?.SetupBeforeStartup();
		}
		Update(1f, 1f, isLast: true, isFirst: true);
		if (IsCorrupted)
		{
			return;
		}
		foreach (Player player in Players)
		{
			if (player.CorrectSpawnPosition && !player.IsDisposed)
			{
				Microsoft.Xna.Framework.Vector2 playerSpawnPoint = GetPlayerSpawnPoint(player.ObjectData.GetWorldPosition());
				player.SetNewWorldPosition(playerSpawnPoint);
			}
		}
		Update(1f, 1f, isLast: true, isFirst: true);
		if (!IsCorrupted)
		{
			Update(1f, 1f, isLast: true, isFirst: true);
			_ = IsCorrupted;
		}
	}

	public void RunStartup()
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: GameWorld.OnStartup() is SERVER/LOCAL ONLY");
		}
		Cheat.World.Reset();
		string[] array = ((string)PropertiesWorld.Get(ObjectPropertyID.World_StartCommands).Value).ToUpperInvariant().Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string command in array)
		{
			RunGameCommand(command, startup: true);
		}
		List<SFD.Weapons.WeaponItem> list = null;
		list = ((Cheat.StartWeapons == null) ? new List<SFD.Weapons.WeaponItem>() : Cheat.StartWeapons);
		foreach (Player player in Players)
		{
			for (int j = 0; j < list.Count; j++)
			{
				if (list[j] != null)
				{
					player.GiveStartWeaponItem(list[j]);
				}
			}
		}
		if (Cheat.StartHealth > 0f && Cheat.StartHealth < 100f)
		{
			foreach (Player player2 in Players)
			{
				player2.Health.CurrentValue = Cheat.StartHealth;
			}
		}
		if (Cheat.SlomoTime != 1f)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Cheat SlomoTime initiated to " + Cheat.SlomoTime);
			SlowmotionHandler.AddSlowmotion(new Slowmotion(100f, 600000f, 1000f, Cheat.SlomoTime, 0));
		}
		StartDefaultScript();
	}

	public void OnStartup()
	{
		if (GameOwner != GameOwnerEnum.Client)
		{
			CallScriptInner(DefaultScript, "OnStartup", showError: false, null);
			SpawnGroupsOnStartup();
		}
		FinalizeProperties();
	}

	public void AfterStartup()
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			return;
		}
		List<ObjectStartupTrigger> objectDataByType = GetObjectDataByType<ObjectStartupTrigger>();
		for (int i = 0; i < objectDataByType.Count; i++)
		{
			ObjectStartupTrigger objectStartupTrigger = objectDataByType[i];
			if (!objectStartupTrigger.IsDisposed && objectStartupTrigger.ActivateAfterStartup)
			{
				objectStartupTrigger.TriggerNode(null);
			}
		}
		CallScriptInner(DefaultScript, "AfterStartup", showError: false, null);
		foreach (SandboxInstance extensionScript in GetExtensionScripts())
		{
			CallScriptInner(extensionScript, "AfterStartup", showError: false, null);
		}
	}

	public void HandleObjectsOnLoad()
	{
		try
		{
			if (GameOwner != GameOwnerEnum.Client)
			{
				PrepareSpawnGroups();
			}
			List<ObjectData> list = new List<ObjectData>(StaticObjects.Count + DynamicObjects.Count);
			foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
			{
				list.Add(staticObject.Value);
			}
			foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
			{
				list.Add(dynamicObject.Value);
			}
			foreach (ObjectData item in list)
			{
				if (item is ObjectPlayerSpawnMarker)
				{
					PlayerSpawnMarkers.Add((ObjectPlayerSpawnMarker)item);
				}
			}
		}
		catch (Exception innerException)
		{
			throw new Exception("Error: GameWorld.HandleObjectsOnLoad() failed", innerException);
		}
	}

	public void DeleteObjectAtCursor()
	{
		Microsoft.Xna.Framework.Vector2 mouseBox2DPosition = GetMouseBox2DPosition();
		List<ObjectData> allObjectsAtPosition = GetAllObjectsAtPosition(mouseBox2DPosition);
		if (allObjectsAtPosition.Count > 0)
		{
			allObjectsAtPosition.Sort(DeleteObjectAtCursorSorting);
			ObjectData objectData = allObjectsAtPosition[0];
			if (DeleteObjectAtCursorConfirmDeletion(objectData) && (m_confirmDeletionObject != objectData || NetTime.Now - m_confirmDeletionObjectTime > 0.35))
			{
				m_confirmDeletionObject = objectData;
				m_confirmDeletionObjectTime = NetTime.Now;
				GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data($"Confirm deletion: ({objectData.ObjectID}) {objectData.MapObjectID}", Microsoft.Xna.Framework.Color.Yellow));
				return;
			}
			GameInfo.ShowChatMessage(new NetMessage.ChatMessage.Data($"Deleting ({objectData.ObjectID}) {objectData.MapObjectID}", Microsoft.Xna.Framework.Color.IndianRed));
			objectData.Destroy();
		}
		m_confirmDeletionObject = null;
	}

	public bool DeleteObjectAtCursorConfirmDeletion(ObjectData od)
	{
		if (!(od.MapObjectID == "SOUNDAREA") && !(od.MapObjectID == "CAMERAAREATRIGGER"))
		{
			return od.MapObjectID == "AREATRIGGER";
		}
		return true;
	}

	public bool DeleteObjectAtCursorAvoidScore(ObjectData od, out int score)
	{
		if (!(od.MapObjectID == "SOUNDAREA") && !(od.MapObjectID == "CAMERAAREATRIGGER") && !(od.MapObjectID == "AREATRIGGER"))
		{
			if (!od.MapObjectID.Contains("PATHNODE") && !od.MapObjectID.Contains("PATHZONE"))
			{
				if (od.MapObjectID.Contains("TRIGGER"))
				{
					score = 500000;
					return true;
				}
				score = 0;
				return false;
			}
			score = 600000;
			return true;
		}
		score = 1000000;
		return true;
	}

	public int DeleteObjectAtCursorSorting(ObjectData odA, ObjectData odB)
	{
		int num = -odA.LocalDrawCategory * 1000 - odA.LocalRenderLayer;
		if (odA.Body.GetType() == Box2D.XNA.BodyType.Static)
		{
			num += 100000;
		}
		int score = 0;
		if (DeleteObjectAtCursorAvoidScore(odA, out score))
		{
			num += score;
		}
		int num2 = -odB.LocalDrawCategory * 1000 + -odB.LocalRenderLayer;
		if (odB.Body.GetType() == Box2D.XNA.BodyType.Static)
		{
			num2 += 100000;
		}
		if (DeleteObjectAtCursorAvoidScore(odB, out score))
		{
			num2 += score;
		}
		if (num < num2)
		{
			return -1;
		}
		if (num > num2)
		{
			return 1;
		}
		return 0;
	}

	public void DrawWorld(double ms)
	{
		float msf = (float)ms * SlowmotionHandler.SlowmotionModifier;
		if (Program.IsGame && (EditMode & !EditPhysicsRunning))
		{
			msf = 0f;
		}
		if (GameOwner == GameOwnerEnum.Client)
		{
			Client.DrawingQueued = true;
			lock (Client.ClientUpdateLockObject)
			{
				CalculateCameraArea((float)ms);
				DrawWorldInner(msf);
				DrawBorderInfo();
				Client.DrawingQueued = false;
				return;
			}
		}
		if (GameOwner == GameOwnerEnum.Local)
		{
			DrawWorldInner(msf);
			DrawBorderInfo();
		}
	}

	public void AddClientHealthCheck(ObjectData od)
	{
		if (!od.IsDisposed && !m_clientHeathChecks.Contains(od))
		{
			od.LastClientHealthCheck = (float)NetTime.Now;
			m_clientHeathChecks.Add(od);
		}
	}

	public void CheckClientHealthChecks()
	{
		if (m_clientHeathChecks.Count <= 0)
		{
			return;
		}
		double now = NetTime.Now;
		for (int num = m_clientHeathChecks.Count - 1; num >= 0; num--)
		{
			ObjectData objectData = m_clientHeathChecks[num];
			if (!objectData.IsDisposed && !objectData.TerminationInitiated)
			{
				if (now - (double)objectData.LastClientHealthCheck >= 0.5)
				{
					if (objectData.Health.IsEmpty)
					{
						objectData.Health.CurrentValue = 1f;
					}
					m_clientHeathChecks.RemoveAt(num);
				}
			}
			else
			{
				m_clientHeathChecks.RemoveAt(num);
			}
		}
	}

	public void UpdatePlayersToFront()
	{
		if (BringPlayerToFront.Count <= 0)
		{
			return;
		}
		List<ObjectData> items = RenderCategories[Category.PLAYERS].GetLayer(0).Items;
		for (int i = 0; i < BringPlayerToFront.Count; i++)
		{
			Player player = BringPlayerToFront[i];
			if (!player.IsRemoved)
			{
				items.Remove(player.ObjectData);
				items.Add(player.ObjectData);
			}
		}
		BringPlayerToFront.Clear();
	}

	public void UpdateSyncedTileAnimations(float msf)
	{
		if (SyncedTileAnimations.Count <= 0)
		{
			return;
		}
		foreach (KeyValuePair<string, TileAnimation> syncedTileAnimation in SyncedTileAnimations)
		{
			syncedTileAnimation.Value.Progress(msf);
		}
	}

	public FixedArray4<int> UpdatePlayersAndGetHighlightObjects(float msf)
	{
		int num = 0;
		FixedArray4<int> result = default(FixedArray4<int>);
		foreach (Player player in Players)
		{
			if (player.IsRemoved)
			{
				continue;
			}
			player.UpdateAnimation();
			player.ObjectData.DrawFlag = true;
			if (!player.IsLocal || num >= 4)
			{
				continue;
			}
			ObjectData closestActivateableObject = player.GetClosestActivateableObject(activateableFlag: false, activateableHighlightFlag: true, 100f, msf);
			if (closestActivateableObject != null)
			{
				closestActivateableObject = closestActivateableObject.GetActivateableHighlightObject(player);
				if (closestActivateableObject != null)
				{
					result[num++] = closestActivateableObject.ObjectID;
				}
			}
		}
		return result;
	}

	public void DrawWeather(float msf)
	{
		if (Weather != null && !EditMode)
		{
			Weather.Draw(msf, Camera.ConvertWorldToScreen(Microsoft.Xna.Framework.Vector2.Zero), Camera.Zoom);
		}
	}

	public void DrawLazer(SpriteBatch spriteBatch, bool strongAlpha, Microsoft.Xna.Framework.Vector2 worldPos1, Microsoft.Xna.Framework.Vector2 worldPos2, Microsoft.Xna.Framework.Vector2 lazerDir)
	{
		Microsoft.Xna.Framework.Vector2 vector = Camera.ConvertWorldToScreen(worldPos2);
		int num = Math.Max((int)Camera.Zoom, 1);
		float num2 = 0f;
		num2 = ((!strongAlpha) ? Constants.RANDOM.NextFloat(0.05f, 0.15f) : Constants.RANDOM.NextFloat(0.3f, 0.5f));
		Microsoft.Xna.Framework.Vector2 vector2 = Camera.ConvertWorldToScreen(worldPos1);
		float x = (vector2 - vector).Length();
		spriteBatch.Draw(Constants.WhitePixel, vector2 + (vector - vector2) * 0.5f, null, ColorCorrection.CreateCustom(Constants.COLORS.Create(Constants.COLORS.LAZER, num2)), (float)Math.Atan2(0f - lazerDir.Y, lazerDir.X), new Microsoft.Xna.Framework.Vector2(0.5f, 0.5f), new Microsoft.Xna.Framework.Vector2(x, num), SpriteEffects.None, 0f);
		vector.X += Constants.RANDOM.NextFloat(-0.7f, 0.7f);
		vector.Y += Constants.RANDOM.NextFloat(-0.7f, 0.7f);
		spriteBatch.Draw(Constants.WhitePixel, vector, null, ColorCorrection.CreateCustom(Constants.COLORS.Create(Constants.COLORS.LAZER, Constants.RANDOM.NextFloat(0.85f, 0.95f))), (float)Math.Atan2(0f - lazerDir.Y, lazerDir.X), new Microsoft.Xna.Framework.Vector2(0.5f, 0.5f), new Microsoft.Xna.Framework.Vector2((float)num * 1.5f, (float)num * 1.5f), SpriteEffects.None, 0f);
	}

	public void CacheLocalPlayerData()
	{
		if (Program.IsGame)
		{
			GameUser gameUser = ((GameInfo != null) ? GameInfo.GetLocalGameUser(0) : null);
			if (gameUser != null)
			{
				m_localGameUserPlayerCache.PlayerID = gameUser.UserIdentifier;
				GUI_TeamDisplay_LocalGameUserTeam = m_localGameUserPlayerCache.Player?.CurrentTeam ?? gameUser.GameSlotTeam;
				GUI_TeamDisplay_LocalGameUserIdentifier = gameUser.UserIdentifier;
			}
			else
			{
				GUI_TeamDisplay_LocalGameUserTeam = Team.Independent;
				GUI_TeamDisplay_LocalGameUserIdentifier = -1;
			}
		}
		else
		{
			GUI_TeamDisplay_LocalGameUserTeam = Team.Independent;
			GUI_TeamDisplay_LocalGameUserIdentifier = -1;
		}
	}

	public void DrawWorldInner(float msf)
	{
		if (IsCorrupted)
		{
			return;
		}
		double num = Math.Min((NetTime.Now - m_drawingBox2DSimulationTimestepOverLastNetTime) * 1000.0, 500.0);
		m_drawingBox2DSimulationTimestepOver = m_drawingBox2DSimulationTimestepOverBase + num * 0.8500000238418579 * 0.001 * 0.955 * m_drawingBox2DSimulationTimestepOverModifier;
		CacheLocalPlayerData();
		UpdatePlayersToFront();
		bool flag = Constants.ENABLE_FARBG;
		if (Program.IsGame & EditMode)
		{
			flag = AllLayersLocked(0);
		}
		if (CheckRescanDrawingZoneRequired())
		{
			PerformRescanDrawingZone();
		}
		bool enabled;
		if (enabled = DebugCamera.Enabled)
		{
			m_debugOrgCamPosition = Camera.Position;
			m_debugOrgCamZoom = Camera.Zoom;
			Camera.SetPositionAndZoom(DebugCamera.Position, DebugCamera.Zoom);
		}
		UpdateSyncedTileAnimations(msf);
		FixedArray4<int> fixedArray = UpdatePlayersAndGetHighlightObjects(msf);
		Microsoft.Xna.Framework.Vector2 position = Camera.Position;
		int num2 = -1;
		List<ObjectData> list = null;
		if ((EditGroupID > 0) & EditMode)
		{
			list = new List<ObjectData>();
		}
		SpriteBatch spriteBatch = m_spriteBatch;
		for (int i = 0; i < 30; i++)
		{
			Camera.m_position = position;
			Camera.PrepareConvertBox2DToScreenValues();
			Layers<ObjectData> layers = RenderCategories[i];
			if (num2 == 0 && (flag & (Weather != null)))
			{
				Weather.DrawBG(msf, Camera.ConvertWorldToScreen(Microsoft.Xna.Framework.Vector2.Zero), Camera.Zoom);
			}
			GameEffects.Draw(spriteBatch, i);
			if (i == 20)
			{
				for (int j = 0; j < Players.Count; j++)
				{
					Players[j].DrawAim(msf, Player.DrawAimMode.Lazer);
				}
				for (int k = 0; k < Projectiles.Count; k++)
				{
					Projectiles[k].Draw(spriteBatch, msf);
				}
				DrawWeather(msf);
			}
			for (int l = 0; l < layers.TotalLayers; l++)
			{
				Camera.m_position = position;
				Camera.PrepareConvertBox2DToScreenValues();
				Layer<ObjectData> layer = layers.GetLayer(l);
				if (i == 0 && flag)
				{
					SFDLayerFarBGTag sFDLayerFarBGTag = (SFDLayerFarBGTag)layer.Tag;
					if (sFDLayerFarBGTag.FarBackgroundBorderEnabled)
					{
						int num3 = (int)Camera.ConvertWorldToScreenY((float)sFDLayerFarBGTag.FarBackgroundBorderPosition + (Camera.Position.Y - Camera.Position.Y * sFDLayerFarBGTag.FarBackgroundMovementFactor));
						spriteBatch.Draw(Constants.WhitePixel, new Rectangle(0, num3, GameSFD.GAME_WIDTH, GameSFD.GAME_HEIGHT - num3), Microsoft.Xna.Framework.Color.Black);
					}
					Camera.m_position = position * sFDLayerFarBGTag.FarBackgroundMovementFactor;
					Camera.PrepareConvertBox2DToScreenValues();
				}
				if (!((i != 0 || flag) | EditMode))
				{
					continue;
				}
				foreach (ObjectData item in layer.Items)
				{
					if (item.DoDrawUpdate)
					{
						item.DrawUpdate(msf);
					}
					if (!(item.DoDraw & !item.UpdateColorsQueued & (item.DrawFlag | DebugCamera.ForceDrawEverything)))
					{
						continue;
					}
					if (!EditMode)
					{
						if ((fixedArray[0] == item.ObjectID) | (fixedArray[1] == item.ObjectID) | (fixedArray[2] == item.ObjectID) | (fixedArray[3] == item.ObjectID))
						{
							item.DrawActivateHightlight(spriteBatch);
						}
						item.Draw(spriteBatch, msf);
					}
					else if (RenderCategories[item.Tile.DrawCategory].GetLayer(item.LocalRenderLayer).IsVisible)
					{
						if ((EditGroupID == 0) | (EditGroupID != item.GroupID))
						{
							item.Draw(spriteBatch, msf);
						}
						else
						{
							list.Add(item);
						}
					}
				}
			}
			num2 = i;
		}
		if (list != null)
		{
			spriteBatch.Draw(Constants.WhitePixel, new Rectangle(0, 0, GameSFD.GAME_WIDTH, GameSFD.GAME_HEIGHT), new Microsoft.Xna.Framework.Color(0f, 0f, 0f, 0.85f));
			foreach (ObjectData item2 in list)
			{
				item2.Draw(spriteBatch, msf);
			}
		}
		if (GameSFD.Handle.CurrentState != State.MainMenu)
		{
			foreach (Player player in Players)
			{
				player.DrawAim(msf, Player.DrawAimMode.ManualAimBox);
				player.DrawPlates(msf);
			}
			if (m_playerPrecacheItemsState < 30)
			{
				if (m_playerPrecacheItemsState == 0)
				{
					foreach (Player player2 in Players)
					{
						player2.PreCacheEquipmentItemTextures(enable: true);
					}
				}
				m_playerPrecacheItemsState++;
				if (m_playerPrecacheItemsState == 30)
				{
					foreach (Player player3 in Players)
					{
						player3.PreCacheEquipmentItemTextures(enable: false);
					}
				}
			}
		}
		else
		{
			foreach (Player player4 in Players)
			{
				player4.DrawPlates(msf);
			}
		}
		DrawDialogues(spriteBatch, msf);
		if (enabled)
		{
			Camera.SetPositionAndZoom(m_debugOrgCamPosition, m_debugOrgCamZoom);
		}
	}

	public void DrawBorderInfo()
	{
		if (EditMode || !(m_activeObjectCameraAreaTrigger.Item?.GetShowDistanceMarkers() ?? GetWorldShowDistanceMarkers()))
		{
			return;
		}
		Area screenWorldBounds = Camera.GetScreenWorldBounds();
		foreach (Player player in Players)
		{
			if (!player.IsDead && !player.IsRemoved && (player.IsBot || player.IsInputEnabled))
			{
				player.DrawDistanceArrow(screenWorldBounds);
			}
		}
	}

	public void DrawEditorAfter()
	{
		if (EditSelectedObjects.Count <= 0)
		{
			return;
		}
		m_spriteBatch.Begin();
		foreach (ObjectData editSelectedObject in EditSelectedObjects)
		{
			editSelectedObject.EditDrawExtraData(m_spriteBatch);
		}
		m_spriteBatch.End();
	}

	public void DrawBox(Microsoft.Xna.Framework.Vector2 centerPoint, Microsoft.Xna.Framework.Vector2 size, float rotation, Microsoft.Xna.Framework.Color color, float thickness)
	{
		Microsoft.Xna.Framework.Vector2 position = -size * 0.5f;
		Microsoft.Xna.Framework.Vector2 position2 = -new Microsoft.Xna.Framework.Vector2(0f - size.X, size.Y) * 0.5f;
		Microsoft.Xna.Framework.Vector2 position3 = size * 0.5f;
		Microsoft.Xna.Framework.Vector2 position4 = new Microsoft.Xna.Framework.Vector2(0f - size.X, size.Y) * 0.5f;
		SFDMath.RotatePosition(ref position, rotation, out position);
		SFDMath.RotatePosition(ref position2, rotation, out position2);
		SFDMath.RotatePosition(ref position4, rotation, out position4);
		SFDMath.RotatePosition(ref position3, rotation, out position3);
		position += centerPoint;
		position2 += centerPoint;
		position3 += centerPoint;
		position4 += centerPoint;
		position = Converter.Box2DToWorld(position);
		position2 = Converter.Box2DToWorld(position2);
		position4 = Converter.Box2DToWorld(position4);
		position3 = Converter.Box2DToWorld(position3);
		DrawLine(m_spriteBatch, position, position2, color, thickness);
		DrawLine(m_spriteBatch, position4, position3, color, thickness);
		DrawLine(m_spriteBatch, position, position4, color, thickness);
		DrawLine(m_spriteBatch, position2, position3, color, thickness);
	}

	public void DrawAABB(SpriteBatch spriteBatch, ref AABB aabb, Microsoft.Xna.Framework.Color color, float thickness)
	{
		List<Microsoft.Xna.Framework.Vector2> list = new List<Microsoft.Xna.Framework.Vector2>();
		list.Add(new Microsoft.Xna.Framework.Vector2(aabb.lowerBound.X, aabb.lowerBound.Y));
		list.Add(new Microsoft.Xna.Framework.Vector2(aabb.upperBound.X, aabb.lowerBound.Y));
		list.Add(new Microsoft.Xna.Framework.Vector2(aabb.upperBound.X, aabb.upperBound.Y));
		list.Add(new Microsoft.Xna.Framework.Vector2(aabb.lowerBound.X, aabb.upperBound.Y));
		list.Add(new Microsoft.Xna.Framework.Vector2(aabb.lowerBound.X, aabb.lowerBound.Y));
		DrawLine(spriteBatch, list, color, thickness);
	}

	public void DrawRectangle(SpriteBatch spriteBatch, Rectangle rectangle, Microsoft.Xna.Framework.Color color, float thickness)
	{
		DrawLine(spriteBatch, rectangle, color, thickness);
	}

	public void DrawArea(SpriteBatch spriteBatch, Area area, Microsoft.Xna.Framework.Color color, float thickness)
	{
		List<Microsoft.Xna.Framework.Vector2> list = new List<Microsoft.Xna.Framework.Vector2>();
		list.Add(area.TopLeft);
		list.Add(area.TopRight);
		list.Add(area.BottomRight);
		list.Add(area.BottomLeft);
		list.Add(area.TopLeft);
		DrawLine(spriteBatch, list, color, thickness);
	}

	public void DrawLine(SpriteBatch spriteBatch, Rectangle rectangle, Microsoft.Xna.Framework.Color color, float thickness)
	{
		List<Microsoft.Xna.Framework.Vector2> list = new List<Microsoft.Xna.Framework.Vector2>();
		list.Add(new Microsoft.Xna.Framework.Vector2(rectangle.X, rectangle.Y));
		list.Add(new Microsoft.Xna.Framework.Vector2(rectangle.X + rectangle.Width, rectangle.Y));
		list.Add(new Microsoft.Xna.Framework.Vector2(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height));
		list.Add(new Microsoft.Xna.Framework.Vector2(rectangle.X, rectangle.Y + rectangle.Height));
		list.Add(new Microsoft.Xna.Framework.Vector2(rectangle.X, rectangle.Y));
		DrawLine(spriteBatch, list, color, thickness);
	}

	public void DrawLine(SpriteBatch spriteBatch, List<Microsoft.Xna.Framework.Vector2> worldPoints, Microsoft.Xna.Framework.Color color, float thickness)
	{
		if (worldPoints != null && worldPoints.Count != 0)
		{
			Microsoft.Xna.Framework.Vector2 worldPos = worldPoints[0];
			for (int i = 1; i < worldPoints.Count; i++)
			{
				Microsoft.Xna.Framework.Vector2 vector = worldPoints[i];
				DrawLine(spriteBatch, worldPos, vector, color, thickness);
				worldPos = vector;
			}
		}
	}

	public void DrawLineTexture(SpriteBatch spriteBatch, Microsoft.Xna.Framework.Vector2 worldPos1, Microsoft.Xna.Framework.Vector2 worldPos2, Microsoft.Xna.Framework.Color color, Texture2D texture, float startOffset = 0f)
	{
		Rectangle bounds = texture.Bounds;
		float num = (worldPos1 - worldPos2).Length();
		Microsoft.Xna.Framework.Vector2 vector = worldPos1 - worldPos2;
		vector.Normalize();
		if (!vector.IsValid())
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 origin = new Microsoft.Xna.Framework.Vector2((float)texture.Width * 0.5f, 0f);
		float num2 = (float)Math.Atan2(vector.Y, vector.X) - (float)Math.PI / 2f;
		float num3 = Math.Max(texture.Height, 1f);
		Area screenWorldBounds = Camera.GetScreenWorldBounds();
		float distanceFromEdge = screenWorldBounds.GetDistanceFromEdge(worldPos1);
		if (distanceFromEdge > num && !DebugCamera.ForceDrawEverything)
		{
			return;
		}
		if ((distanceFromEdge > num3) & !DebugCamera.ForceDrawEverything)
		{
			worldPos1 -= vector * (int)(distanceFromEdge / num3) * num3;
		}
		distanceFromEdge = screenWorldBounds.GetDistanceFromEdge(worldPos2);
		if (distanceFromEdge > num && !DebugCamera.ForceDrawEverything)
		{
			return;
		}
		if ((distanceFromEdge > num3) & !DebugCamera.ForceDrawEverything)
		{
			worldPos2 += vector * (int)(distanceFromEdge / num3) * num3;
		}
		float num4 = (worldPos1 - worldPos2).Length();
		if (num4 <= 0f || num3 < 1f || float.IsInfinity(num4) || float.IsNaN(num4) || !(num4 <= 1600f))
		{
			return;
		}
		vector.Y *= -1f;
		worldPos1 = Camera.ConvertWorldToScreen(worldPos1);
		float num5 = num3 * Camera.Zoom;
		if (!(num3 <= 0.1f))
		{
			startOffset %= (float)texture.Height;
			if (startOffset < 0f)
			{
				startOffset += (float)texture.Height;
			}
			if (startOffset > 0f)
			{
				bounds.Height = (int)Math.Round(Math.Min(num4 - startOffset, startOffset), 0);
				bounds.Y = texture.Height - bounds.Height;
				spriteBatch.Draw(texture, worldPos1, bounds, color, 0f - num2, origin, Camera.ZoomUpscaled, SpriteEffects.None, 0f);
			}
			bounds.Y = 0;
			float num6 = startOffset / (float)texture.Height;
			for (float num7 = startOffset; num7 < num4; num7 += num3)
			{
				bounds.Height = (int)Math.Round(Math.Min(num4 - num7, texture.Height), 0);
				spriteBatch.Draw(texture, worldPos1 - vector * num5 * num6, bounds, color, 0f - num2, origin, Camera.ZoomUpscaled, SpriteEffects.None, 0f);
				num6 += 1f;
			}
		}
	}

	public void DrawEditArrowLine(SpriteBatch spriteBatch, ObjectData fromObj, ObjectData toObj, Microsoft.Xna.Framework.Color color, float thickness, float shorteningDistance = 3f)
	{
		Microsoft.Xna.Framework.Vector2 pA = fromObj.GetWorldCenterPosition();
		if (fromObj is ObjectTransparent && ((ObjectTransparent)fromObj).AreaIcon != null)
		{
			pA = fromObj.GetWorldPosition();
		}
		Microsoft.Xna.Framework.Vector2 pB = toObj.GetWorldCenterPosition();
		if (toObj is ObjectTransparent && ((ObjectTransparent)toObj).AreaIcon != null)
		{
			pB = toObj.GetWorldPosition();
		}
		DrawEditArrowLine(spriteBatch, pA, pB, color, thickness, shorteningDistance);
	}

	public void DrawEditArrowLine(SpriteBatch spriteBatch, Microsoft.Xna.Framework.Vector2 pA, Microsoft.Xna.Framework.Vector2 pB, Microsoft.Xna.Framework.Color color, float thickness, float shorteningDistance = 3f)
	{
		Microsoft.Xna.Framework.Vector2 vector = pB - pA;
		if (vector.CalcSafeLength() > shorteningDistance * 4f)
		{
			vector.Normalize();
			if (vector.IsValid())
			{
				pB -= vector * shorteningDistance;
				pA += vector * shorteningDistance;
			}
		}
		DrawLine(spriteBatch, pA, pB, color, thickness);
		if (vector.IsValid())
		{
			vector.Normalize();
			Microsoft.Xna.Framework.Vector2 position = vector;
			Microsoft.Xna.Framework.Vector2 position2 = vector;
			SFDMath.RotatePosition(ref position, Microsoft.Xna.Framework.MathHelper.ToRadians(22f), out position);
			SFDMath.RotatePosition(ref position2, 0f - Microsoft.Xna.Framework.MathHelper.ToRadians(22f), out position2);
			DrawLine(spriteBatch, pB, pB - position * 4.5f, color, thickness);
			DrawLine(spriteBatch, pB, pB - position2 * 4.5f, color, thickness);
		}
	}

	public void DrawLine(SpriteBatch spriteBatch, Microsoft.Xna.Framework.Vector2 worldPos1, Microsoft.Xna.Framework.Vector2 worldPos2, Microsoft.Xna.Framework.Color color, float thickness)
	{
		Microsoft.Xna.Framework.Vector2 vector = Camera.ConvertWorldToScreen(worldPos2);
		Microsoft.Xna.Framework.Vector2 vector2 = Camera.ConvertWorldToScreen(worldPos1);
		Microsoft.Xna.Framework.Vector2 vector3 = vector2 - vector;
		float x = (vector2 - vector).Length();
		spriteBatch.Draw(Constants.WhitePixel, vector2 + (vector - vector2) * 0.5f, null, color, (float)Math.Atan2(vector3.Y, vector3.X), new Microsoft.Xna.Framework.Vector2(0.5f, 0.5f), new Microsoft.Xna.Framework.Vector2(x, thickness), SpriteEffects.None, 0f);
	}

	public void InvalidateFlags()
	{
		b2_flagsSet = false;
	}

	public void DisposeDebugPathNodeData()
	{
		if (m_debugPathNodePath != null)
		{
			m_debugPathNodePath.Free();
		}
		m_debugPathNodePath = null;
	}

	public bool DebugMouseFilter(ObjectData filterOd)
	{
		if (!(filterOd.MapObjectID == "SOUNDAREA") && !(filterOd.MapObjectID == "CAMERAAREATRIGGER") && !(filterOd.MapObjectID == "AREATRIGGER") && !(filterOd.MapObjectID == "GIBZONE") && !(filterOd.MapObjectID == "GIBZONECLEAN"))
		{
			return true;
		}
		return false;
	}

	public void UpdateDebugMouse()
	{
		if (SFD.Input.Mouse.LeftButton.IsPressed)
		{
			if (SFD.Input.Keyboard.IsCtrlDown())
			{
				if (!m_debugPathNodeDrawing)
				{
					m_debugPathNodeDrawing = true;
					m_debugPathNodeStartBox2DPos = GetMouseBox2DPosition();
				}
				return;
			}
			m_debugPathNodeDrawing = false;
			if (m_debugMouseObject == null)
			{
				ObjectData objectAtMousePosition = GetObjectAtMousePosition(autoUnlockLockedLayers: true, autoShowHiddenLayers: true, prioritizeDynamicObjects: true, DebugMouseFilter);
				if (objectAtMousePosition != null && objectAtMousePosition.IsDynamic)
				{
					m_debugMouseObject = objectAtMousePosition;
				}
				if (m_debugMouseObject != null)
				{
					m_debugMouseObject.Body.SetAwake(flag: true);
					ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Adding debugmouse object");
					MouseJointDef mouseJointDef = new MouseJointDef();
					mouseJointDef.target = GetMouseBox2DPosition();
					mouseJointDef.localAnchor = m_debugMouseObject.Body.GetLocalPoint(mouseJointDef.target);
					float num = m_debugMouseObject.Body.GetMass();
					List<Body> connectedWeldedBodies = m_debugMouseObject.Body.GetConnectedWeldedBodies();
					if (connectedWeldedBodies != null)
					{
						for (int i = 0; i < connectedWeldedBodies.Count; i++)
						{
							num += connectedWeldedBodies[i].GetMass();
						}
					}
					mouseJointDef.maxForce = num * 150f;
					mouseJointDef.dampingRatio = 1f;
					mouseJointDef.frequencyHz = 40f;
					mouseJointDef.collideConnected = false;
					m_debugMouseWorld = m_debugMouseObject.Body.GetWorld();
					mouseJointDef.bodyA = m_debugMouseWorld.GroundBody;
					mouseJointDef.bodyB = m_debugMouseObject.Body;
					m_debugMouseJoint = (MouseJoint)m_debugMouseObject.Body.GetWorld().CreateJoint(mouseJointDef);
				}
			}
			if (m_debugMouseObject == null || m_debugMouseObject.IsDisposed)
			{
				return;
			}
			m_debugMouseJoint.SetTarget(GetMouseBox2DPosition());
			if (m_debugMouseObject.IsPlayer)
			{
				Player player = (Player)m_debugMouseObject.InternalData;
				if (!player.IsRemoved && !player.Falling)
				{
					player.Fall();
				}
			}
			return;
		}
		if (m_debugPathNodeDrawing)
		{
			DisposeDebugPathNodeData();
			m_debugPathNodeDrawing = false;
			m_debugPathNodeEndBox2DPos = GetMouseBox2DPosition();
			PathNode pathNode = PathGrid.FindClosestPathNode(m_debugPathNodeStartBox2DPos);
			PathNode pathNode2 = PathGrid.FindClosestPathNode(m_debugPathNodeEndBox2DPos);
			if (pathNode != null && pathNode2 != null && pathNode != pathNode2)
			{
				int pathSteps = 0;
				PathNode solutionEndNode = null;
				m_debugPathNodePath = PathGrid.FindPath(pathNode, pathNode2, out pathSteps, out solutionEndNode);
			}
		}
		if (m_debugMouseObject != null)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Releasing debugmouse object");
			if (m_debugMouseJoint != null && !m_debugMouseObject.IsDisposed)
			{
				m_debugMouseWorld.DestroyJoint(m_debugMouseJoint);
			}
			m_debugMouseJoint = null;
			m_debugMouseObject = null;
		}
	}

	public ObjectData GetObjectAtMousePosition(bool autoUnlockLockedLayers, bool autoShowHiddenLayers, bool prioritizeDynamicObjects, Func<ObjectData, bool> filterFunc = null)
	{
		Microsoft.Xna.Framework.Vector2 mouseBox2DPosition = GetMouseBox2DPosition();
		return GetObjectAtPosition(mouseBox2DPosition, autoUnlockLockedLayers, autoShowHiddenLayers, prioritizeDynamicObjects, EditGroupID, filterFunc);
	}

	public Microsoft.Xna.Framework.Vector2 GetMouseGamePosition()
	{
		if (m_game.CurrentState == State.Editor)
		{
			return StateEditor.MousePosition;
		}
		return new Microsoft.Xna.Framework.Vector2(GameSFD.CURSOR_GAME_X, GameSFD.CURSOR_GAME_Y);
	}

	public Microsoft.Xna.Framework.Vector2 GetMouseWorldPosition()
	{
		Microsoft.Xna.Framework.Vector2 mouseGamePosition = GetMouseGamePosition();
		if (DebugCamera.Enabled)
		{
			Microsoft.Xna.Framework.Vector2 position = Camera.Position;
			float zoom = Camera.Zoom;
			Camera.m_position = DebugCamera.Position;
			Camera.m_zoom = DebugCamera.Zoom;
			Microsoft.Xna.Framework.Vector2 result = Camera.ConvertScreenToWorld(mouseGamePosition);
			Camera.m_position = position;
			Camera.m_zoom = zoom;
			return result;
		}
		return Camera.ConvertScreenToWorld(mouseGamePosition);
	}

	public Microsoft.Xna.Framework.Vector2 GetMouseBox2DPosition()
	{
		Microsoft.Xna.Framework.Vector2 mouseGamePosition = GetMouseGamePosition();
		if (DebugCamera.Enabled)
		{
			Microsoft.Xna.Framework.Vector2 position = Camera.Position;
			float zoom = Camera.Zoom;
			Camera.m_position = DebugCamera.Position;
			Camera.m_zoom = DebugCamera.Zoom;
			Microsoft.Xna.Framework.Vector2 result = Camera.ConvertScreenToBox2D(mouseGamePosition);
			Camera.m_position = position;
			Camera.m_zoom = zoom;
			return result;
		}
		return Camera.ConvertScreenToBox2D(mouseGamePosition);
	}

	public List<ObjectData> GetAllObjectsAtPosition(Microsoft.Xna.Framework.Vector2 box2DPos)
	{
		Microsoft.Xna.Framework.Vector2 worldPosition = Converter.Box2DToWorld(box2DPos);
		HashSet<ObjectData> results = new HashSet<ObjectData>();
		AABB.Create(out var aabb, box2DPos, box2DPos, 0.2f);
		World[] array = new World[2] { b2_world_active, b2_world_background };
		for (int i = 0; i < array.Length; i++)
		{
			array[i].QueryAABB(delegate(Fixture fixture)
			{
				if (fixture != null && fixture.GetUserData() != null && fixture.TestPoint(box2DPos))
				{
					ObjectData objectData = ObjectData.Read(fixture);
					if (EditCheckTouch(objectData, worldPosition))
					{
						results.Add(objectData);
					}
				}
				return true;
			}, ref aabb);
		}
		return results.ToList();
	}

	public ObjectData GetObjectAtPosition(Microsoft.Xna.Framework.Vector2 box2DPos, bool autoUnlockLockedLayers, bool autoShowHiddenLayers, bool prioritizeDynamicObjects, ushort groupID, Func<ObjectData, bool> filterFunc = null)
	{
		Microsoft.Xna.Framework.Vector2 worldPosition = Converter.Box2DToWorld(box2DPos);
		AABB.Create(out var aabb, box2DPos, box2DPos, 0.2f);
		ObjectData hitObject = null;
		ObjectData backupHitObject = null;
		World[] array = new World[2] { b2_world_active, b2_world_background };
		for (int i = 0; i < array.Length; i++)
		{
			array[i].QueryAABB(delegate(Fixture fixture)
			{
				if (fixture != null && fixture.GetUserData() != null && fixture.TestPoint(box2DPos))
				{
					ObjectData objectData = ObjectData.Read(fixture);
					bool flag = false;
					if (groupID > 0 && objectData.GroupID != groupID)
					{
						flag = true;
					}
					else if (filterFunc != null)
					{
						flag = !filterFunc(objectData);
					}
					if (!flag)
					{
						if (objectData.LocalRenderLayer != -1)
						{
							LayerStatus layerStatus = EditGetLayerStatus(objectData);
							if (autoUnlockLockedLayers)
							{
								layerStatus.IsLocked = false;
							}
							if (autoShowHiddenLayers)
							{
								layerStatus.IsVisible = true;
							}
							if (!layerStatus.IsLocked && layerStatus.IsVisible && (hitObject == null || (prioritizeDynamicObjects && hitObject.Body.GetType() == Box2D.XNA.BodyType.Static && objectData.Body.GetType() == Box2D.XNA.BodyType.Dynamic) || objectData.Tile.DrawCategory > hitObject.Tile.DrawCategory || (objectData.Tile.DrawCategory == hitObject.Tile.DrawCategory && objectData.LocalRenderLayer > hitObject.LocalRenderLayer) || (objectData.Tile.DrawCategory == hitObject.Tile.DrawCategory && objectData.LocalRenderLayer == hitObject.LocalRenderLayer && objectData.GetZOrder() > hitObject.GetZOrder())) && (!prioritizeDynamicObjects || hitObject == null || hitObject.Body.GetType() == Box2D.XNA.BodyType.Static || objectData.Body.GetType() == Box2D.XNA.BodyType.Dynamic) && EditCheckTouch(objectData, worldPosition))
							{
								hitObject = objectData;
							}
						}
						else if (backupHitObject == null)
						{
							backupHitObject = objectData;
						}
					}
				}
				return true;
			}, ref aabb);
		}
		if (hitObject == null)
		{
			return backupHitObject;
		}
		return hitObject;
	}

	public void DrawDebugMouse(bool drawShape, bool drawData, bool autoUnlockLockedLayers, bool autoShowHiddenLayers)
	{
		if (!drawShape && !drawData)
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 mouseGamePosition = GetMouseGamePosition();
		Microsoft.Xna.Framework.Vector2 mouseBox2DPosition = GetMouseBox2DPosition();
		ObjectData objectData = null;
		objectData = GetObjectAtPosition(mouseBox2DPosition, autoUnlockLockedLayers, autoShowHiddenLayers, prioritizeDynamicObjects: false, EditGroupID);
		if (objectData == null)
		{
			return;
		}
		if (drawShape)
		{
			Microsoft.Xna.Framework.Color color = new Microsoft.Xna.Framework.Color(0.45f, 0.2f, 0.2f, 0.45f);
			if (m_editSelectionFitler.CheckTargetIncluded(objectData))
			{
				color = new Microsoft.Xna.Framework.Color(0.2f, 0.45f, 0.2f, 0.45f);
			}
			objectData.Body.GetTransform(out var xf);
			for (Fixture fixture = objectData.Body.GetFixtureList(); fixture != null; fixture = fixture.GetNext())
			{
				objectData.Body.GetWorld().DrawShape(fixture, xf, color);
			}
		}
		if (!drawData)
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 vector = mouseGamePosition;
		int x = (int)vector.X - 20;
		int num = (int)vector.Y + 20;
		int num2 = (int)Constants.MeasureString(DebugDraw._font, "ID\nID").Y / 2;
		for (ushort num3 = 0; num3 < 5; num3++)
		{
			switch (num3)
			{
			case 0:
			{
				string text = objectData.ObjectID.ToString();
				string text2 = objectData.CustomIDName;
				if (!string.IsNullOrEmpty(text2))
				{
					if (text2.Length > 20)
					{
						text2 = text2.Substring(0, 18) + "..";
					}
					text = text + " '" + text2 + "'";
				}
				b2_debugDraw.DrawString(x, num, new Microsoft.Xna.Framework.Color(1f, 1f, 0.6f, 1f), LanguageHelper.GetText("mapEditor.tooltop.objectNameAndID", (objectData.Tile != null) ? objectData.Tile.Name : objectData.MapObjectID, text));
				num += num2;
				break;
			}
			case 1:
			{
				if (objectData.GroupID <= 0)
				{
					break;
				}
				string text3 = objectData.GroupID.ToString();
				GroupInfo value = null;
				if (GroupInfo.TryGetValue(objectData.GroupID, out value))
				{
					string text4 = ((value.Marker != null) ? value.Marker.CustomIDName : "?");
					if (!string.IsNullOrEmpty(text4))
					{
						if (text4.Length > 20)
						{
							text4 = text4.Substring(0, 18) + "..";
						}
						text3 = text3 + " '" + text4 + "'";
					}
				}
				b2_debugDraw.DrawString(x, num, Microsoft.Xna.Framework.Color.LightBlue, LanguageHelper.GetText("mapEditor.tooltop.memberInGroup", text3));
				num += num2;
				break;
			}
			case 2:
			{
				objectData.GetObjectSize(out var widthX, out var heightY);
				if (widthX > 0 || heightY > 0)
				{
					b2_debugDraw.DrawString(x, num, Microsoft.Xna.Framework.Color.White, LanguageHelper.GetText("mapEditor.tooltop.sizeXY", widthX.ToString(), heightY.ToString()));
					num += num2;
				}
				break;
			}
			case 3:
			{
				Microsoft.Xna.Framework.Vector2 worldPosition = objectData.GetWorldPosition();
				b2_debugDraw.DrawString(x, num, Microsoft.Xna.Framework.Color.White, LanguageHelper.GetText("mapEditor.tooltop.positionXY", $"{worldPosition.X:0.#}", $"{worldPosition.Y:0.#}"));
				num += num2;
				break;
			}
			case 4:
			{
				float angle = objectData.GetAngle();
				angle %= (float)Math.PI * 2f;
				if (Math.Abs(angle) > 0.001f && angle + 0.001f < (float)Math.PI * 2f)
				{
					b2_debugDraw.DrawString(x, num, Microsoft.Xna.Framework.Color.White, LanguageHelper.GetText("mapEditor.tooltop.angleDegAndRad", $"{Converter.ToDegrees(angle):0.0}", $"{angle:0.00}"));
					num += num2;
				}
				break;
			}
			}
		}
	}

	public void DrawDebugMouseJoint()
	{
		Microsoft.Xna.Framework.Vector2 mouseBox2DPosition = GetMouseBox2DPosition();
		if (m_debugMouseObject != null && !m_debugMouseObject.IsDisposed)
		{
			if (m_debugMouseJoint.GetBodyB() != null)
			{
				Microsoft.Xna.Framework.Vector2 anchorB = m_debugMouseJoint.GetAnchorB();
				b2_debugDraw.DrawSegment(anchorB, mouseBox2DPosition, Microsoft.Xna.Framework.Color.Orange);
			}
		}
		else if (m_debugPathNodeDrawing)
		{
			b2_debugDraw.DrawSegment(m_debugPathNodeStartBox2DPos, GetMouseBox2DPosition(), Microsoft.Xna.Framework.Color.LightBlue);
		}
	}

	public void DrawThumbnailViewFinder()
	{
		DrawDebug(DrawDebugFlags.DrawThumbnailViewFinder);
	}

	public void DrawDebug(DrawDebugFlags drawDebugFlags)
	{
		bool flag = (drawDebugFlags & DrawDebugFlags.DrawActive) != 0;
		bool flag2 = (drawDebugFlags & DrawDebugFlags.DrawBackground) != 0;
		bool drawData = (drawDebugFlags & DrawDebugFlags.DrawDebugMouseData) != 0;
		bool drawShape = (drawDebugFlags & DrawDebugFlags.DrawDebugMouseShape) != 0;
		bool flag3 = (drawDebugFlags & DrawDebugFlags.DrawDebugMouseLine) != 0;
		bool flag4 = (drawDebugFlags & DrawDebugFlags.DrawThumbnailViewFinder) != 0;
		bool flag5 = (drawDebugFlags & DrawDebugFlags.DrawScriptDebugInformation) != 0;
		if (Program.IsGame && m_editTargetObjectPropertyInstance != null)
		{
			drawShape = true;
			drawData = true;
		}
		if (Program.IsGame)
		{
			if ((m_game.CurrentState == State.Editor || m_game.CurrentState == State.EditorTestRun) && SFD.Input.Keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightAlt))
			{
				ToggleDrawShapes(1u);
				flag = true;
				drawData = true;
				drawShape = true;
			}
			if (m_game.CurrentState == State.Editor)
			{
				drawData = true;
			}
		}
		if (!b2_flagsSet)
		{
			uint num = 0u;
			num = 0 + b2_settings.drawShapes;
			num += b2_settings.drawJoints * 2;
			num += b2_settings.drawAABBs * 4;
			num += b2_settings.drawPairs * 8;
			num += b2_settings.drawCOMs * 16;
			b2_debugDraw.Flags = (DebugDrawFlags)num;
			b2_flagsSet = true;
		}
		Microsoft.Xna.Framework.Vector2 position = Camera.Position;
		float zoom = Camera.Zoom;
		if (DebugCamera.Enabled)
		{
			position = DebugCamera.Position;
			zoom = DebugCamera.Zoom;
		}
		Microsoft.Xna.Framework.Vector2 vector = new Microsoft.Xna.Framework.Vector2(GameSFD.GAME_WIDTH2f * 0.04f, GameSFD.GAME_HEIGHT2f * 0.04f);
		vector *= 1f / zoom;
		Microsoft.Xna.Framework.Vector2 vector2 = Converter.ConvertWorldToBox2D(position);
		Microsoft.Xna.Framework.Vector2 vector3 = vector2 - vector;
		Microsoft.Xna.Framework.Vector2 vector4 = vector2 + vector;
		b2_simpleColorEffect.Projection = Matrix.CreateOrthographicOffCenter(vector3.X, vector4.X, vector3.Y, vector4.Y, -1f, 1f);
		b2_simpleColorEffect.Techniques[0].Passes[0].Apply();
		try
		{
			if (Program.IsGame)
			{
				DrawDebugMouse(drawShape, drawData, autoUnlockLockedLayers: false, SFD.Input.Keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightAlt));
				if (flag3)
				{
					DrawDebugMouseJoint();
				}
				if (flag4)
				{
					AABB.Create(out var aabb, vector2, 0f);
					float num2 = 7.22f * (1f / zoom);
					float num3 = 14.42f * (1f / zoom);
					aabb.lowerBound.Y -= num2;
					aabb.upperBound.Y += num2;
					aabb.lowerBound.X -= num3;
					aabb.upperBound.X += num3;
					b2_debugDraw.DrawAABB(ref aabb, Microsoft.Xna.Framework.Color.White);
				}
				bool flag6 = false;
				int num4 = 1;
				bool flag7 = false;
				bool flag8 = false;
				bool flag9 = false;
				if (EditMode)
				{
					flag6 = EditDrawGrid;
					num4 = EditGridSize;
					flag7 = EditDrawCenter;
					flag8 = EditDrawWorldZones;
					flag9 = EditDrawPathGrid;
				}
				if (flag6 && num4 > 1)
				{
					float num5 = Converter.ConvertWorldToBox2D(num4);
					Microsoft.Xna.Framework.Color gRID_COLOR = Constants.EDITOR.GRID_COLOR;
					for (float num6 = Converter.ConvertWorldToBox2D((float)Math.Floor(Camera.WorldTop / (float)num4) * (float)num4); num6 > vector3.Y; num6 -= num5)
					{
						b2_debugDraw.DrawSegment(new Microsoft.Xna.Framework.Vector2(vector3.X, num6), new Microsoft.Xna.Framework.Vector2(vector4.X, num6), gRID_COLOR);
					}
					for (float num7 = Converter.ConvertWorldToBox2D((float)Math.Floor(Camera.WorldLeft / (float)num4) * (float)num4); num7 < vector4.X; num7 += num5)
					{
						b2_debugDraw.DrawSegment(new Microsoft.Xna.Framework.Vector2(num7, vector3.Y), new Microsoft.Xna.Framework.Vector2(num7, vector4.Y), gRID_COLOR);
					}
				}
				if (flag7)
				{
					Microsoft.Xna.Framework.Vector2 vector5 = Converter.ConvertWorldToBox2D(Microsoft.Xna.Framework.Vector2.Zero);
					if (vector5.X > vector3.X && vector5.X < vector4.X)
					{
						b2_debugDraw.DrawSegment(new Microsoft.Xna.Framework.Vector2(vector5.X, vector3.Y), new Microsoft.Xna.Framework.Vector2(vector5.X, vector4.Y), Constants.EDITOR.WORLD_CENTER_V_COLOR);
					}
					if (vector5.Y > vector3.Y && vector5.Y < vector4.Y)
					{
						b2_debugDraw.DrawSegment(new Microsoft.Xna.Framework.Vector2(vector3.X, vector5.Y), new Microsoft.Xna.Framework.Vector2(vector4.X, vector5.Y), Constants.EDITOR.WORLD_CENTER_H_COLOR);
					}
				}
				if (flag8)
				{
					DrawDebugWorldRectangle(WorldCameraMaxArea, Constants.EDITOR.WORLD_BORDERS_COLOR);
					DrawDebugWorldRectangle(WorldCameraSafeArea, Constants.EDITOR.WORLD_CAMERA_COLOR);
					float num8 = Converter.ConvertWorldToBox2D(BoundsWorldBottom);
					if (num8 > vector3.Y && num8 < vector4.Y)
					{
						b2_debugDraw.DrawSegment(new Microsoft.Xna.Framework.Vector2(vector3.X, num8), new Microsoft.Xna.Framework.Vector2(vector4.X, num8), Constants.EDITOR.WORLD_BOUNDS_COLOR);
					}
				}
				if (GameOwner != GameOwnerEnum.Client)
				{
					if (flag9 | BotDebugOptions.ShowPathGrid.Enabled)
					{
						try
						{
							List<Microsoft.Xna.Framework.Vector2> list = null;
							HashSet<long> hashSet = new HashSet<long>();
							foreach (KeyValuePair<int, PathNode> pathNode in PathGrid.PathNodes)
							{
								Microsoft.Xna.Framework.Vector2 vector6 = Converter.Box2DToWorld(pathNode.Value.Box2DPosition);
								vector6.X = (float)Math.Round(vector6.X, 0);
								vector6.Y = (float)Math.Round(vector6.Y, 0);
								long item = ((long)vector6.X << 32) | ((long)vector6.Y & 0x7FFFFFFFL);
								if (!hashSet.Add(item))
								{
									if (list == null)
									{
										list = new List<Microsoft.Xna.Framework.Vector2>();
									}
									list.Add(pathNode.Value.Box2DPosition);
								}
							}
							if (list != null)
							{
								foreach (Microsoft.Xna.Framework.Vector2 item3 in list)
								{
									b2_debugDraw.DrawCircle(item3, 0.19999999f, Microsoft.Xna.Framework.Color.Red);
								}
							}
							foreach (KeyValuePair<int, PathNodeConnection> pathNodeConnection in PathGrid.PathNodeConnections)
							{
								if (!pathNodeConnection.Value.Connected)
								{
									continue;
								}
								Microsoft.Xna.Framework.Color color = PathNodeConnection.RenderColor(pathNodeConnection.Value.ConnectionType);
								if (!pathNodeConnection.Value.CheckConnectionEnabled())
								{
									color = Microsoft.Xna.Framework.Color.Gray;
								}
								if (pathNodeConnection.Value.Direction == PathNodeConnectionDirection.OneWay)
								{
									Microsoft.Xna.Framework.Vector2 vector7 = Microsoft.Xna.Framework.Vector2.Normalize(pathNodeConnection.Value.PathNodeB.ScanConnectionsBox2DPosition - pathNodeConnection.Value.PathNodeA.ScanConnectionsBox2DPosition);
									if (vector7.IsValid())
									{
										Microsoft.Xna.Framework.Vector2 vector8 = vector7;
										SFDMath.RotateVector90CW(ref vector8, out vector8);
										vector8 *= 0.02f;
										color = new Microsoft.Xna.Framework.Color(Math.Max(0, color.R - 60), Math.Max(0, color.G - 50), Math.Max(0, color.B - 40), color.A);
										b2_debugDraw.DrawSegment(pathNodeConnection.Value.PathNodeA.ScanConnectionsBox2DPosition + vector8, pathNodeConnection.Value.PathNodeB.ScanConnectionsBox2DPosition + vector8, color);
										Microsoft.Xna.Framework.Vector2 vector9 = pathNodeConnection.Value.PathNodeB.ScanConnectionsBox2DPosition - vector7 * 0.04f * 3f;
										Microsoft.Xna.Framework.Vector2 position2 = vector7;
										Microsoft.Xna.Framework.Vector2 position3 = vector7;
										SFDMath.RotatePosition(ref position2, Microsoft.Xna.Framework.MathHelper.ToRadians(22f), out position2);
										SFDMath.RotatePosition(ref position3, 0f - Microsoft.Xna.Framework.MathHelper.ToRadians(22f), out position3);
										b2_debugDraw.DrawSegment(vector9 + vector8, vector9 - position2 * 0.04f * 4.5f + vector8, color);
										b2_debugDraw.DrawSegment(vector9 + vector8, vector9 - position3 * 0.04f * 4.5f + vector8, color);
									}
								}
								else
								{
									b2_debugDraw.DrawSegment(pathNodeConnection.Value.PathNodeA.ScanConnectionsBox2DPosition, pathNodeConnection.Value.PathNodeB.ScanConnectionsBox2DPosition, color);
								}
								if (pathNodeConnection.Value.HasActivators)
								{
									Microsoft.Xna.Framework.Vector2 box2DPositionMidNodes = pathNodeConnection.Value.GetBox2DPositionMidNodes();
									b2_debugDraw.DrawCircle(box2DPositionMidNodes, 0.06f, Microsoft.Xna.Framework.Color.Yellow);
								}
							}
							foreach (KeyValuePair<int, PathNode> pathNode2 in PathGrid.PathNodes)
							{
								b2_debugDraw.DrawCircle(pathNode2.Value.Box2DPosition, 0.12f, PathNode.RenderColor(pathNode2.Value.NodeType));
								if (pathNode2.Value.ChokePoint)
								{
									b2_debugDraw.DrawCircle(pathNode2.Value.Box2DPosition, 0.17999999f, Microsoft.Xna.Framework.Color.LightBlue);
								}
								if (pathNode2.Value.UseDynamicConnections)
								{
									Microsoft.Xna.Framework.Vector2 box2DPosition = pathNode2.Value.Box2DPosition;
									Microsoft.Xna.Framework.Vector2 position4 = new Microsoft.Xna.Framework.Vector2(1f);
									SFDMath.RotatePosition(ref position4, pathNode2.Value.Angle, out position4);
									b2_debugDraw.DrawSegment(box2DPosition + position4 * 0.04f * 2.5f, box2DPosition + position4 * 0.04f * 5f, Microsoft.Xna.Framework.Color.LightBlue);
									b2_debugDraw.DrawSegment(box2DPosition - position4 * 0.04f * 2.5f, box2DPosition - position4 * 0.04f * 5f, Microsoft.Xna.Framework.Color.LightBlue);
									position4 = new Microsoft.Xna.Framework.Vector2(-1f, 1f);
									SFDMath.RotatePosition(ref position4, pathNode2.Value.Angle, out position4);
									b2_debugDraw.DrawSegment(box2DPosition + position4 * 0.04f * 2.5f, box2DPosition + position4 * 0.04f * 5f, Microsoft.Xna.Framework.Color.LightBlue);
									b2_debugDraw.DrawSegment(box2DPosition - position4 * 0.04f * 2.5f, box2DPosition - position4 * 0.04f * 5f, Microsoft.Xna.Framework.Color.LightBlue);
								}
								if (pathNode2.Value.IsElevatorNode)
								{
									b2_debugDraw.DrawCircle(pathNode2.Value.Box2DPosition, 0.14999999f, Microsoft.Xna.Framework.Color.Yellow);
								}
								Microsoft.Xna.Framework.Vector2 orientation = pathNode2.Value.GetOrientation();
								if (Microsoft.Xna.Framework.Vector2.Dot(orientation, Microsoft.Xna.Framework.Vector2.UnitY) < 0.998f)
								{
									b2_debugDraw.DrawSegment(pathNode2.Value.Box2DPosition, pathNode2.Value.Box2DPosition + orientation * 0.04f * 5f, Microsoft.Xna.Framework.Color.White);
								}
								if (!pathNode2.Value.Enabled)
								{
									Microsoft.Xna.Framework.Vector2 box2DPosition2 = pathNode2.Value.Box2DPosition;
									Microsoft.Xna.Framework.Vector2 vector10 = new Microsoft.Xna.Framework.Vector2(1f) * 0.04f * 1.5f;
									b2_debugDraw.DrawSegment(box2DPosition2 + vector10, box2DPosition2 - vector10, Microsoft.Xna.Framework.Color.Gray);
									vector10 = new Microsoft.Xna.Framework.Vector2(-1f, 1f) * 0.04f * 1.5f;
									b2_debugDraw.DrawSegment(box2DPosition2 + vector10, box2DPosition2 - vector10, Microsoft.Xna.Framework.Color.Gray);
								}
								if (pathNode2.Value.BlockedUpwards)
								{
									Microsoft.Xna.Framework.Vector2 vector11 = pathNode2.Value.Box2DPosition + Microsoft.Xna.Framework.Vector2.UnitY * 0.04f * 5f;
									Microsoft.Xna.Framework.Vector2 vector12 = Microsoft.Xna.Framework.Vector2.UnitX * 0.04f * 1.5f;
									b2_debugDraw.DrawSegment(vector11 + vector12, vector11 - vector12, Microsoft.Xna.Framework.Color.Red);
								}
							}
							SimpleLinkedList<ListPathPointNode> debugPathNodePath = m_debugPathNodePath;
							if (debugPathNodePath != null && m_debugPathNodePathRenderTime > ElapsedTotalRealTime)
							{
								Microsoft.Xna.Framework.Vector2 vector13 = new Microsoft.Xna.Framework.Vector2(0.04f, 0.04f);
								for (SimpleLinkedList<ListPathPointNode> simpleLinkedList = debugPathNodePath; simpleLinkedList != null; simpleLinkedList = simpleLinkedList.Next)
								{
									ListPathPointNode item2 = simpleLinkedList.Item;
									if (item2 != null && item2.ConnectionToNext != null && item2.ConnectionToNext.Connected)
									{
										b2_debugDraw.DrawSegment(item2.ConnectionToNext.PathNodeA.Box2DPosition + vector13, item2.ConnectionToNext.PathNodeB.Box2DPosition + vector13, Microsoft.Xna.Framework.Color.Magenta);
									}
								}
							}
							else if (m_debugPathNodePathRenderTime < ElapsedTotalRealTime - 200f)
							{
								m_debugPathNodePathRenderTime = ElapsedTotalRealTime + 200f;
							}
						}
						catch (Exception ex)
						{
							if (GameOwner != GameOwnerEnum.Server)
							{
								throw ex;
							}
						}
					}
					if (BotDebugOptions.BotPathFinding.Enabled)
					{
						foreach (Player player in Players)
						{
							if (!player.IsDisposed && !player.IsDead && player.IsBot && player.BotBehaviorActive)
							{
								player.DrawDebugBotNavPath(b2_debugDraw, BotDebugOptions.BotPathFindingExtra.Enabled);
							}
						}
					}
					if (BotDebugOptions.BotLineOfSight.Enabled)
					{
						foreach (Player player2 in Players)
						{
							if (!player2.IsDisposed && !player2.IsDead)
							{
								player2.DrawDebugBotLOS(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotItemSearchRange.Enabled)
					{
						foreach (Player player3 in Players)
						{
							if (!player3.IsDisposed && !player3.IsDead)
							{
								player3.DrawDebugBotSearchItemRange(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotChaseRange.Enabled)
					{
						foreach (Player player4 in Players)
						{
							if (!player4.IsDisposed && !player4.IsDead)
							{
								player4.DrawDebugBotChaseRange(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotTeamLineUp.Enabled)
					{
						foreach (Player player5 in Players)
						{
							if (!player5.IsDisposed && !player5.IsDead)
							{
								player5.DrawDebugBotTeamLineUp(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotGuardRange.Enabled)
					{
						foreach (Player player6 in Players)
						{
							if (!player6.IsDisposed && !player6.IsDead)
							{
								player6.DrawDebugBotGuardRange(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotAggroRange.Enabled)
					{
						foreach (Player player7 in Players)
						{
							if (!player7.IsDisposed && !player7.IsDead)
							{
								player7.DrawDebugBotAggroRange(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotGuardPosition.Enabled)
					{
						foreach (Player player8 in Players)
						{
							if (!player8.IsDisposed && !player8.IsDead)
							{
								player8.DrawDebugBotGuardPosition(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.BotCommandQueue.Enabled)
					{
						foreach (Player player9 in Players)
						{
							if (!player9.IsDisposed)
							{
								player9.DrawDebugBotCommandQueue(b2_debugDraw);
							}
						}
					}
					if (DebugBotPathFinding.Enabled)
					{
						foreach (Player player10 in Players)
						{
							if (!player10.IsDisposed && !player10.IsDead)
							{
								player10.DrawDebugBotPathFinding(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.PlayerQueuedActions.Enabled)
					{
						foreach (Player player11 in Players)
						{
							if (!player11.IsDisposed && !player11.IsDead)
							{
								player11.DrawDebugQueuedKeys(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.Streetsweepers.Enabled)
					{
						foreach (ObjectStreetsweeper item4 in GetObjectDataByType<ObjectStreetsweeper>())
						{
							if (!item4.IsDisposed)
							{
								item4.DrawDebug(b2_debugDraw);
							}
						}
					}
					if (BotDebugOptions.StreetsweepersPathFinding.Enabled)
					{
						foreach (ObjectStreetsweeper item5 in GetObjectDataByType<ObjectStreetsweeper>())
						{
							if (!item5.IsDisposed)
							{
								item5.DrawDebugPath(b2_debugDraw, BotDebugOptions.StreetsweepersPathFinding.DebugColor);
							}
						}
					}
					if (BotDebugOptions.ThrownItems.Enabled)
					{
						foreach (ObjectData missileUpdateObject in GetMissileUpdateObjects())
						{
							if (missileUpdateObject.MissileData != null)
							{
								Microsoft.Xna.Framework.Vector2 vector14 = Camera.ConvertWorldToScreen(missileUpdateObject.GetWorldCenterPosition());
								Microsoft.Xna.Framework.Color c = BotDebugOptions.ThrownItems.DebugColor;
								switch (missileUpdateObject.MissileData.Status)
								{
								case ObjectMissileStatus.Debris:
									c = Microsoft.Xna.Framework.Color.LightBlue;
									break;
								case ObjectMissileStatus.Dropped:
									c = Microsoft.Xna.Framework.Color.Yellow;
									break;
								case ObjectMissileStatus.Thrown:
									c = new Microsoft.Xna.Framework.Color(255, 80, 80);
									break;
								}
								b2_debugDraw.DrawString((int)vector14.X, (int)vector14.Y + 10, c, missileUpdateObject.MissileData.Status.ToString());
							}
						}
					}
				}
				if (flag)
				{
					b2_world_active.DrawDebugData();
				}
				if (flag2)
				{
					b2_world_background.DrawDebugData();
				}
				if (flag)
				{
					float cover_s = m_lastUpdateTime / 1000f;
					foreach (Projectile projectile in Projectiles)
					{
						projectile.GetRayCastInput(out var rayCastInput, cover_s);
						b2_debugDraw.DrawSegment(rayCastInput.p1, rayCastInput.p2, Microsoft.Xna.Framework.Color.Yellow);
						projectile.GetAABB(out var aabb2, cover_s);
						b2_debugDraw.DrawAABB(ref aabb2, Constants.COLORS.RED);
					}
					foreach (KeyValuePair<int, ObjectData> dynamicObject in DynamicObjects)
					{
						if (dynamicObject.Value.Activateable || dynamicObject.Value.ActivateableHighlightning)
						{
							b2_debugDraw.DrawCircle(dynamicObject.Value.Body.GetPosition(), Converter.WorldToBox2D(dynamicObject.Value.ActivateRange), new Microsoft.Xna.Framework.Color(0, 200, 200, 200));
						}
					}
					foreach (KeyValuePair<int, ObjectData> staticObject in StaticObjects)
					{
						if (staticObject.Value.Activateable || staticObject.Value.ActivateableHighlightning)
						{
							b2_debugDraw.DrawCircle(staticObject.Value.Body.GetPosition(), Converter.WorldToBox2D(staticObject.Value.ActivateRange), new Microsoft.Xna.Framework.Color(0, 200, 200, 200));
						}
					}
					for (int num9 = m_recentExplosions.Count - 1; num9 >= 0; num9--)
					{
						foreach (ExplosionData.ExplosionLine line in m_recentExplosions[num9].Lines)
						{
							b2_debugDraw.DrawSegment(line.RayCastInput.p1, line.RayCastInput.p2, Microsoft.Xna.Framework.Color.Red);
							b2_debugDraw.DrawSegment(line.RayCastInputToNext.p1, line.RayCastInputToNext.p2, Microsoft.Xna.Framework.Color.Red);
						}
					}
				}
				if (EditMode)
				{
					if (EditPreviewObjects.Count > 0)
					{
						foreach (ObjectData editPreviewObject in EditPreviewObjects)
						{
							b2_world_active.DrawDebugBody(editPreviewObject.Body);
						}
					}
					bool flag10;
					if (flag10 = SFD.Input.Keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Space))
					{
						EditHighlightObjectsOnce.Clear();
					}
					else if (EditSelectedObjects.Count > 0)
					{
						Dictionary<ushort, Area> dictionary = new Dictionary<ushort, Area>();
						foreach (ObjectData editSelectedObject in EditSelectedObjects)
						{
							EditHighlightObjectsOnce.Remove(editSelectedObject);
							FixedArray4<Microsoft.Xna.Framework.Vector2> fixedArray = editSelectedObject.DrawEditHighlight(b2_world_active);
							if (editSelectedObject.GroupID <= 0)
							{
								continue;
							}
							Area value = null;
							if (!dictionary.TryGetValue(editSelectedObject.GroupID, out value))
							{
								value = new Area(fixedArray[0], 0f);
								dictionary.Add(editSelectedObject.GroupID, value);
							}
							for (int i = 0; i < 4; i++)
							{
								if (value.Left > fixedArray[i].X)
								{
									value.Left = fixedArray[i].X;
								}
								if (value.Right < fixedArray[i].X)
								{
									value.Right = fixedArray[i].X;
								}
								if (value.Bottom > fixedArray[i].Y)
								{
									value.Bottom = fixedArray[i].Y;
								}
								if (value.Top < fixedArray[i].Y)
								{
									value.Top = fixedArray[i].Y;
								}
							}
						}
						if (dictionary.Count > 0 && EditGroupID == 0)
						{
							foreach (KeyValuePair<ushort, Area> item6 in dictionary)
							{
								DrawDebugBox2DRectangle(item6.Value, Microsoft.Xna.Framework.Color.LightBlue);
							}
						}
						if (EditSelectedObjects.Count == 1)
						{
							ObjectData objectData = EditSelectedObjects[0];
							if (objectData.Tile.Sizeable != Tile.SIZEABLE.N)
							{
								Microsoft.Xna.Framework.Vector2 result = Microsoft.Xna.Framework.Vector2.Zero;
								if (objectData.GetSizeablePosition(Tile.SIZEABLE.H, out result))
								{
									DrawDebugWorldRectangle(result, 1.5f, Microsoft.Xna.Framework.Color.White);
								}
								if (objectData.GetSizeablePosition(Tile.SIZEABLE.V, out result))
								{
									DrawDebugWorldRectangle(result, 1.5f, Microsoft.Xna.Framework.Color.White);
								}
								if (objectData.GetSizeablePosition(Tile.SIZEABLE.D, out result))
								{
									DrawDebugWorldRectangle(result, 1.5f, Microsoft.Xna.Framework.Color.White);
								}
							}
						}
						if (EditSelectedObjects.Count > 0 && m_editRotationInAction)
						{
							Microsoft.Xna.Framework.Vector2 vector15 = EditGetActiveCenterOfSelection();
							float num10 = 0.05f;
							b2_world_active.DebugDraw.DrawSegment(vector15 - new Microsoft.Xna.Framework.Vector2(num10, num10), vector15 + new Microsoft.Xna.Framework.Vector2(num10, num10), Microsoft.Xna.Framework.Color.White);
							b2_world_active.DebugDraw.DrawSegment(vector15 - new Microsoft.Xna.Framework.Vector2(-0.05f, num10), vector15 + new Microsoft.Xna.Framework.Vector2(-0.05f, num10), Microsoft.Xna.Framework.Color.White);
						}
						HashSet<ObjectData> hashSet2 = null;
						if (EditHighlightObjectsOnce.Count > 0)
						{
							hashSet2 = new HashSet<ObjectData>();
							foreach (ObjectData item7 in EditHighlightObjectsOnce)
							{
								if (!hashSet2.Contains(item7) && item7.Body != null)
								{
									b2_world_active.DrawDebugBody(item7.Body);
									hashSet2.Add(item7);
								}
							}
							EditHighlightObjectsOnce.Clear();
						}
						if (!flag10 && EditHighlightObjectsFixed.Count > 0)
						{
							if (hashSet2 == null)
							{
								hashSet2 = new HashSet<ObjectData>();
							}
							foreach (Tuple<ObjectData, ObjectData> item8 in EditHighlightObjectsFixed)
							{
								if (!hashSet2.Contains(item8.Item2) && item8.Item2.Body != null)
								{
									b2_world_active.DrawDebugBody(item8.Item2.Body);
									hashSet2.Add(item8.Item2);
								}
								b2_world_active.DebugDraw.DrawArrowLine(item8.Item2.GetBox2DPosition(), item8.Item1.GetBox2DPosition(), Microsoft.Xna.Framework.Color.LightGray, 0.12f);
							}
						}
						if (hashSet2 != null)
						{
							hashSet2.Clear();
							hashSet2 = null;
						}
					}
					if (m_editLeftMoveAction == EditLeftMoveAction.Select && EditSelectionArea.IsValidSelection)
					{
						Microsoft.Xna.Framework.Vector2 vector16 = Converter.ConvertWorldToBox2D(EditSelectionArea.GetTopRight());
						Microsoft.Xna.Framework.Vector2 vector17 = Converter.ConvertWorldToBox2D(EditSelectionArea.GetBottomLeft());
						Microsoft.Xna.Framework.Vector2 p = new Microsoft.Xna.Framework.Vector2(vector17.X, vector16.Y);
						Microsoft.Xna.Framework.Vector2 vector18 = new Microsoft.Xna.Framework.Vector2(vector16.X, vector16.Y);
						Microsoft.Xna.Framework.Vector2 vector19 = new Microsoft.Xna.Framework.Vector2(vector17.X, vector17.Y);
						Microsoft.Xna.Framework.Vector2 p2 = new Microsoft.Xna.Framework.Vector2(vector16.X, vector17.Y);
						b2_world_active.DebugDraw.DrawSegment(p, vector18, Microsoft.Xna.Framework.Color.DarkGreen);
						b2_world_active.DebugDraw.DrawSegment(vector19, p2, Microsoft.Xna.Framework.Color.DarkGreen);
						b2_world_active.DebugDraw.DrawSegment(p, vector19, Microsoft.Xna.Framework.Color.DarkGreen);
						b2_world_active.DebugDraw.DrawSegment(vector18, p2, Microsoft.Xna.Framework.Color.DarkGreen);
					}
				}
				if (flag5)
				{
					for (int j = 0; j < m_scriptDebugLinesCount; j++)
					{
						ScriptDebugLine scriptDebugLine = m_scriptDebugLines[j];
						b2_world_active.DebugDraw.DrawSegment(scriptDebugLine.Point1, scriptDebugLine.Point2, scriptDebugLine.Color);
					}
					for (int k = 0; k < m_scriptDebugCirclesCount; k++)
					{
						ScriptDebugCircle scriptDebugCircle = m_scriptDebugCircles[k];
						b2_world_active.DebugDraw.DrawCircle(scriptDebugCircle.Center, scriptDebugCircle.Radius, scriptDebugCircle.Color);
					}
					for (int l = 0; l < m_scriptDebugAreasCount; l++)
					{
						ScriptDebugArea scriptDebugArea = m_scriptDebugAreas[l];
						b2_world_active.DebugDraw.DrawAABB(ref scriptDebugArea.AABB, scriptDebugArea.Color);
					}
					for (int m = 0; m < m_scriptDebugTextsCount; m++)
					{
						ScriptDebugText scriptDebugText = m_scriptDebugTexts[m];
						Microsoft.Xna.Framework.Vector2 vector20 = Camera.ConvertBox2DToScreen(scriptDebugText.Position);
						((DebugDraw)b2_world_active.DebugDraw).DrawString((int)vector20.X, (int)vector20.Y, scriptDebugText.Color, scriptDebugText.Text);
					}
				}
			}
		}
		catch (ThreadAbortException)
		{
			throw;
		}
		catch
		{
			if (EditDrawGrid)
			{
				Camera.SetZoom(1f);
				EditGridSize = 8;
			}
		}
		finally
		{
			b2_debugDraw.FinishDrawShapes();
		}
		m_spriteBatch.Begin();
		try
		{
			b2_debugDraw.FinishDrawString();
		}
		catch (ThreadAbortException)
		{
			throw;
		}
		catch
		{
		}
		m_spriteBatch.End();
	}

	public void DrawDebugBox2DRectangle(Area rect, Microsoft.Xna.Framework.Color color)
	{
		Microsoft.Xna.Framework.Vector2 topLeft = rect.TopLeft;
		Microsoft.Xna.Framework.Vector2 topRight = rect.TopRight;
		Microsoft.Xna.Framework.Vector2 bottomLeft = rect.BottomLeft;
		Microsoft.Xna.Framework.Vector2 bottomRight = rect.BottomRight;
		b2_debugDraw.DrawSegment(topLeft, topRight, color);
		b2_debugDraw.DrawSegment(bottomLeft, bottomRight, color);
		b2_debugDraw.DrawSegment(topLeft, bottomLeft, color);
		b2_debugDraw.DrawSegment(topRight, bottomRight, color);
	}

	public void DrawDebugWorldRectangle(Microsoft.Xna.Framework.Vector2 pos, float extent, Microsoft.Xna.Framework.Color color)
	{
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertWorldToBox2D(pos);
		Microsoft.Xna.Framework.Vector2 vector2 = vector;
		Microsoft.Xna.Framework.Vector2 vector3 = vector;
		Microsoft.Xna.Framework.Vector2 p = vector;
		extent = Converter.WorldToBox2D(extent);
		vector.X -= extent;
		vector.Y += extent;
		vector2.X += extent;
		vector2.Y += extent;
		vector3.X -= extent;
		vector3.Y -= extent;
		p.X += extent;
		p.Y -= extent;
		b2_debugDraw.DrawSegment(vector, vector2, color);
		b2_debugDraw.DrawSegment(vector3, p, color);
		b2_debugDraw.DrawSegment(vector, vector3, color);
		b2_debugDraw.DrawSegment(vector2, p, color);
	}

	public void DrawDebugWorldRectangle(Area rect, Microsoft.Xna.Framework.Color color)
	{
		Microsoft.Xna.Framework.Vector2 p = Converter.ConvertWorldToBox2D(rect.TopLeft);
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertWorldToBox2D(rect.TopRight);
		Microsoft.Xna.Framework.Vector2 vector2 = Converter.ConvertWorldToBox2D(rect.BottomLeft);
		Microsoft.Xna.Framework.Vector2 p2 = Converter.ConvertWorldToBox2D(rect.BottomRight);
		b2_debugDraw.DrawSegment(p, vector, color);
		b2_debugDraw.DrawSegment(vector2, p2, color);
		b2_debugDraw.DrawSegment(p, vector2, color);
		b2_debugDraw.DrawSegment(vector, p2, color);
	}

	public void RegisterDebris(ObjectData od)
	{
		m_debrisNew.Add(od);
	}

	public void UpdateDebris()
	{
		if (m_debrisNew.Count > 0)
		{
			foreach (ObjectData item in m_debrisNew)
			{
				m_debrisCurrent.Add(item);
			}
			m_debrisNew.Clear();
		}
		if (m_debrisCurrent.Count <= 0)
		{
			return;
		}
		for (int num = m_debrisCurrent.Count - 1; num >= 0; num--)
		{
			if (m_debrisCurrent[num].IsDisposed)
			{
				m_debrisCurrent.RemoveAt(num);
			}
		}
		while (m_debrisCurrent.Count > 120)
		{
			ObjectData objectData = m_debrisCurrent[0];
			m_debrisCurrent.RemoveAt(0);
			objectData.Remove();
		}
	}

	public void SpawnUnsyncedShell(string shellID, Microsoft.Xna.Framework.Vector2 position, float angle, short faceDirection, Microsoft.Xna.Framework.Vector2 linearVelocity)
	{
		CreateLocalTile(shellID, position, angle, faceDirection, linearVelocity, Constants.RANDOM.NextFloat(-1f, 1f));
	}

	public void SpawnDebris(ObjectData sourceObject, Microsoft.Xna.Framework.Vector2 center, float radius, string[] tileIDs, short faceDirection = 1, bool handleAsDebris = true)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			return;
		}
		for (int i = 0; i < tileIDs.Length; i++)
		{
			string text = tileIDs[i];
			float num = (float)Math.PI / 2f + (float)Math.PI * 2f / (float)tileIDs.Length * (float)i;
			radius = Constants.RANDOM.NextFloat(0.75f, 1f) * radius;
			Microsoft.Xna.Framework.Vector2 worldValue = new Microsoft.Xna.Framework.Vector2(center.X + (float)Math.Cos(num) * radius * (float)faceDirection, center.Y + (float)Math.Sin(num) * radius);
			worldValue = Converter.WorldToBox2D(worldValue);
			Microsoft.Xna.Framework.Vector2 vector = Microsoft.Xna.Framework.Vector2.Zero;
			float num2 = 9999f;
			Fixture fixture = sourceObject.Body.GetFixtureList();
			while (fixture != null)
			{
				if (!fixture.TestPoint(worldValue))
				{
					if (fixture.GetShape().ShapeType == ShapeType.Polygon)
					{
						PolygonShape polygonShape = (PolygonShape)fixture.GetShape();
						for (int num3 = polygonShape.GetVertexCount() - 1; num3 >= 0; num3--)
						{
							Microsoft.Xna.Framework.Vector2 vertex = polygonShape.GetVertex(num3);
							vertex = sourceObject.Body.GetWorldPoint(vertex);
							float num4 = Microsoft.Xna.Framework.Vector2.Distance(vertex, worldValue);
							if (num4 < num2)
							{
								vector = vertex;
								num2 = num4;
							}
						}
					}
					else if (fixture.GetShape().ShapeType == ShapeType.Circle)
					{
						float num5 = Converter.Box2DToWorld(((CircleShape)fixture.GetShape())._radius);
						if (radius > num5)
						{
							worldValue = new Microsoft.Xna.Framework.Vector2(center.X + (float)Math.Cos(num) * (num5 - 0.2f), center.Y + (float)Math.Sin(num) * (num5 - 0.2f));
							worldValue = Converter.WorldToBox2D(worldValue);
						}
					}
					fixture = fixture.GetNext();
					continue;
				}
				num2 = 9999f;
				break;
			}
			if (num2 < 9999f)
			{
				worldValue = vector;
			}
			worldValue = Converter.Box2DToWorld(worldValue);
			float num6 = Constants.RANDOM.NextFloat(3.8f, 4.2f);
			Microsoft.Xna.Framework.Vector2 position = new Microsoft.Xna.Framework.Vector2((float)Math.Cos(num) * num6, (float)Math.Sin(num) * num6);
			position += sourceObject.GetLinearVelocity() * 0.5f;
			SFDMath.RotatePosition(ref position, Constants.RANDOM.NextFloat(-(float)Math.PI / 4f, (float)Math.PI / 4f), out position);
			position.X *= faceDirection;
			float angularVelocity = Constants.RANDOM.NextFloat(-4f, 4f);
			if (text != "")
			{
				SpawnObjectInformation spawnObjectInformation = new SpawnObjectInformation(CreateObjectData(text), worldValue, Constants.RANDOM.NextFloat(-(float)Math.PI / 8f, (float)Math.PI / 8f), faceDirection, position, angularVelocity);
				spawnObjectInformation.FireBurning = sourceObject.Fire.IsBurning;
				spawnObjectInformation.FireSmoking = sourceObject.Fire.IsSmoking;
				ObjectData od = ObjectData.Read(CreateTile(spawnObjectInformation));
				if (handleAsDebris)
				{
					RegisterDebris(od);
				}
			}
		}
	}

	public void HandleClosedDialogueItem(DialogueItem item)
	{
		if (m_newDialogues.Remove(item))
		{
			m_dialogues.Remove(item);
			item.Dispose();
		}
		else
		{
			m_closedDialogues.Add(item);
		}
	}

	public void InitializeDialogueData()
	{
		m_newDialogues = new List<DialogueItem>();
		m_dialogues = new List<DialogueItem>();
		m_closedDialogues = new List<DialogueItem>();
		m_addedDialogueIDs = new HashSet<int>();
		m_nextDialogueID = 1;
	}

	public void DisposeDialogueData()
	{
		if (m_newDialogues != null)
		{
			m_newDialogues.Clear();
			m_newDialogues = null;
		}
		if (m_closedDialogues != null)
		{
			m_closedDialogues.Clear();
			m_closedDialogues = null;
		}
		if (m_dialogues != null)
		{
			foreach (DialogueItem dialogue in m_dialogues)
			{
				if (!dialogue.IsDisposed)
				{
					dialogue.Dispose();
				}
			}
			m_dialogues.Clear();
			m_dialogues = null;
		}
		if (m_addedDialogueIDs != null)
		{
			m_addedDialogueIDs.Clear();
			m_addedDialogueIDs = null;
		}
	}

	public void CloseDialogue(int id)
	{
		if (m_dialogues.Count <= 0)
		{
			return;
		}
		List<DialogueItem> list = new List<DialogueItem>();
		foreach (DialogueItem dialogue in m_dialogues)
		{
			if (dialogue.ID == id)
			{
				list.Add(dialogue);
			}
		}
		foreach (DialogueItem item in list)
		{
			item.Close();
			m_dialogues.Remove(item);
		}
	}

	public List<DialogueItem> GetActiveDialogues()
	{
		return m_dialogues;
	}

	public DialogueItem NewDialogue(DialogueArgs args, int id = 0)
	{
		DialogueItem dialogueItem = new DialogueItem();
		if (id == 0)
		{
			dialogueItem.ID = Interlocked.Increment(ref m_nextDialogueID);
		}
		else
		{
			dialogueItem.ID = id;
		}
		if (id != 0 && !m_addedDialogueIDs.Add(dialogueItem.ID))
		{
			return null;
		}
		dialogueItem.TargetObjectID = args.TargetObjectID;
		dialogueItem.SourceWorldPosition = args.WorldPosition;
		dialogueItem.DialogueText = args.DialogueText;
		dialogueItem.DisplayTime = args.DisplayTime;
		dialogueItem.ShowInChat = args.ShowInChat;
		dialogueItem.ElapsedTime = 0f;
		dialogueItem.GameOwner = GameOwner;
		dialogueItem.GameWorld = this;
		dialogueItem.TextColor = args.TextColor;
		if (string.IsNullOrEmpty(dialogueItem.DialogueText))
		{
			dialogueItem.DialogueText = "";
		}
		if (dialogueItem.DialogueText.Length > 200)
		{
			dialogueItem.DialogueText = dialogueItem.DialogueText.Substring(0, 200);
		}
		if (dialogueItem.DisplayTime < 0f)
		{
			dialogueItem.DisplayTime = 3000f + 5000f * (float)(dialogueItem.DialogueText.Length / 200);
		}
		dialogueItem.InitTargetObject();
		if (dialogueItem.TargetObject != null)
		{
			dialogueItem.SourceWorldPosition = dialogueItem.TargetObjectLastWorldPosition;
			for (int num = m_newDialogues.Count - 1; num >= 0; num--)
			{
				if (m_newDialogues[num] != null && m_newDialogues[num].TargetObject == dialogueItem.TargetObject)
				{
					m_newDialogues[num].Dispose();
					m_newDialogues.RemoveAt(num);
				}
			}
			for (int num2 = m_dialogues.Count - 1; num2 >= 0; num2--)
			{
				if (m_dialogues[num2] != null && m_dialogues[num2].TargetObject == dialogueItem.TargetObject)
				{
					CloseDialogue(m_dialogues[num2].ID);
				}
			}
		}
		m_newDialogues.Add(dialogueItem);
		m_dialogues.Add(dialogueItem);
		dialogueItem.SourceName = args.SourceName;
		if (string.IsNullOrEmpty(dialogueItem.SourceName) && dialogueItem.TargetObject != null && dialogueItem.TargetObject.IsPlayer)
		{
			dialogueItem.SourceName = ((Player)dialogueItem.TargetObject.InternalData).Name;
		}
		if (string.IsNullOrEmpty(dialogueItem.SourceName))
		{
			dialogueItem.SourceName = "";
		}
		if (GameOwner != GameOwnerEnum.Server && dialogueItem.ShowInChat)
		{
			string text = TextMeta.EscapeText(dialogueItem.SourceName) + ": " + TextMeta.EscapeText(dialogueItem.DialogueText);
			text = text.Replace("\n", " ");
			NetMessage.ChatMessage.Data data = new NetMessage.ChatMessage.Data(text, Microsoft.Xna.Framework.Color.White, "", isMetaText: true, 0);
			GameInfo.ShowChatMessage(data);
		}
		return dialogueItem;
	}

	public void UpdateDialogues(float ms)
	{
		SyncNewDialogues();
		if (m_dialogues.Count > 0)
		{
			foreach (DialogueItem dialogue in m_dialogues)
			{
				if (!dialogue.IsDisposed)
				{
					dialogue.Update(ms);
				}
			}
		}
		if (m_closedDialogues.Count <= 0)
		{
			return;
		}
		foreach (DialogueItem closedDialogue in m_closedDialogues)
		{
			if (GameOwner == GameOwnerEnum.Server)
			{
				SyncDialogueItem(closedDialogue, isNew: false);
			}
			m_dialogues.Remove(closedDialogue);
			if (!closedDialogue.IsDisposed)
			{
				closedDialogue.Dispose();
			}
		}
		m_closedDialogues.Clear();
	}

	public void DrawDialogues(SpriteBatch spriteBatch, float ms)
	{
		if (m_dialogues.Count <= 0)
		{
			return;
		}
		foreach (DialogueItem dialogue in m_dialogues)
		{
			if (!dialogue.IsDisposed)
			{
				dialogue.Draw(spriteBatch, ms);
			}
		}
	}

	public void SyncNewDialogues()
	{
		if (m_newDialogues.Count <= 0)
		{
			return;
		}
		if (GameOwner == GameOwnerEnum.Server)
		{
			foreach (DialogueItem newDialogue in m_newDialogues)
			{
				SyncDialogueItem(newDialogue, isNew: true);
			}
		}
		m_newDialogues.Clear();
	}

	public void SyncDialogueItem(DialogueItem d, bool isNew)
	{
		m_game.Server?.SyncDialogueItem(d, isNew);
	}

	public int TriggerExplosion(Microsoft.Xna.Framework.Vector2 worldPosition, float explosionDamage, bool serverOnly)
	{
		m_currentExplosionCount++;
		if (m_queuedExplosions.Count <= 0 && m_explosionsTriggeredThisCycle < 1)
		{
			m_explosionsTriggeredThisCycle++;
			TriggerExplosionInner(worldPosition, 36f, explosionDamage, serverOnly, m_currentExplosionCount);
		}
		else
		{
			m_queuedExplosions.Enqueue(new ItemContainer<Microsoft.Xna.Framework.Vector2, float, bool, int>(worldPosition, explosionDamage, serverOnly, m_currentExplosionCount));
		}
		return m_currentExplosionCount;
	}

	public int TriggerExplosion(Microsoft.Xna.Framework.Vector2 worldPosition, float explosionDamage)
	{
		return TriggerExplosion(worldPosition, explosionDamage, serverOnly: false);
	}

	public void HandleQueuedExplosions()
	{
		while (m_explosionsTriggeredThisCycle < 1 && m_queuedExplosions.Count > 0)
		{
			ItemContainer<Microsoft.Xna.Framework.Vector2, float, bool, int> itemContainer = m_queuedExplosions.Dequeue();
			TriggerExplosionInner(itemContainer.Item1, 36f, itemContainer.Item2, itemContainer.Item3, itemContainer.Item4);
			m_explosionsTriggeredThisCycle++;
		}
	}

	public void UpdateRecentExplosions(float totalMs)
	{
		for (int num = m_recentExplosions.Count - 1; num >= 0; num--)
		{
			ExplosionData explosionData = m_recentExplosions[num];
			if (explosionData.AffectedObjects.Count > 0 || explosionData.FirstCycle)
			{
				if (GameOwner != GameOwnerEnum.Client && ScriptCallbackExists_ExplosionHit)
				{
					RunScriptOnExplosionHitCallbacks(explosionData);
				}
				explosionData.AffectedObjects.Clear();
			}
			explosionData.Time += totalMs;
			explosionData.Cycles--;
			explosionData.FirstCycle = false;
			if (explosionData.Time >= 150f && explosionData.Cycles <= 0)
			{
				m_recentExplosions.RemoveAt(num);
			}
		}
	}

	public void TriggerExplosionCheckNewCreatedObject(ObjectData od)
	{
		if (m_recentExplosions.Count <= 0)
		{
			return;
		}
		foreach (ExplosionData recentExplosion in m_recentExplosions)
		{
			recentExplosion.AffectObject(od, CurrentExplosionCount);
		}
	}

	public void TriggerExplosionInner(Microsoft.Xna.Framework.Vector2 worldPosition, float worldRadius, float explosionDamage, bool serverOnly, int explosionCount)
	{
		List<Pair<ObjectData, Explosion>> list = new List<Pair<ObjectData, Explosion>>();
		if (GameOwner != GameOwnerEnum.Server || serverOnly)
		{
			SoundHandler.PlaySound("Explosion", worldPosition, this);
			EffectHandler.PlayEffect("EXP", worldPosition, this);
			EffectHandler.PlayEffect("CAM_S", worldPosition, this, 8f, 250f, false);
		}
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertWorldToBox2D(worldPosition);
		float num = Converter.ConvertWorldToBox2D(worldRadius);
		int num2 = ++m_explosionDataInstanceID;
		Microsoft.Xna.Framework.Vector2 vector2 = new Microsoft.Xna.Framework.Vector2(num, num);
		AABB aabb = default(AABB);
		aabb.lowerBound = vector - vector2;
		aabb.upperBound = vector + vector2;
		aabb.Grow(0.02f);
		List<ExplosionData.ExplosionLine> list2 = new List<ExplosionData.ExplosionLine>();
		List<ExplosionData.ExplosionLineBuilder> inputs = new List<ExplosionData.ExplosionLineBuilder>(36);
		for (int i = 0; i < 36; i++)
		{
			float num3 = (float)Math.PI / 18f * (float)i + (float)Math.PI / 36f;
			Microsoft.Xna.Framework.Vector2 vector3 = new Microsoft.Xna.Framework.Vector2((float)Math.Cos(num3), (float)Math.Sin(num3));
			ExplosionData.ExplosionLineBuilder item = new ExplosionData.ExplosionLineBuilder(vector3, new Box2D.XNA.RayCastInput
			{
				p1 = vector,
				maxFraction = 1f,
				p2 = vector + vector3 * num
			});
			inputs.Add(item);
		}
		HashSet<ObjectData> objectDataToDamage = new HashSet<ObjectData>();
		GetActiveWorld.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture != null && fixture.GetUserData() != null)
			{
				ObjectData objectData = ObjectData.Read(fixture);
				bool flag2 = false;
				if (fixture.BlockExplosions)
				{
					if (!objectData.TerminationInitiated)
					{
						flag2 = true;
						for (int j = 0; j < inputs.Count; j++)
						{
							ExplosionData.ExplosionLineBuilder explosionLineBuilder = inputs[j];
							if (fixture.RayCast(out var output, ref explosionLineBuilder.RayCastInput))
							{
								explosionLineBuilder.HitResults.Add(new ItemContainer<float, Fixture, ObjectData>(output.fraction, fixture, objectData));
							}
						}
					}
				}
				else
				{
					flag2 = true;
				}
				if (flag2 && !objectDataToDamage.Contains(objectData))
				{
					objectDataToDamage.Add(objectData);
				}
			}
			return true;
		}, ref aabb);
		foreach (ExplosionData.ExplosionLineBuilder item3 in inputs)
		{
			item3.SortHits();
		}
		foreach (ExplosionData.ExplosionLineBuilder item4 in inputs)
		{
			float num4 = 1f;
			for (int num5 = 0; num5 < item4.HitResults.Count; num5++)
			{
				ObjectData item2 = item4.HitResults[num5].Item3;
				if (item2.TerminationInitiated)
				{
					continue;
				}
				if (objectDataToDamage.Contains(item2))
				{
					objectDataToDamage.Remove(item2);
					if (item2.DoExplosionHit)
					{
						float explosionImpactPercentage = ExplosionData.CalcExplosionImpactPercentage(item4.HitResults[num5].Item1);
						Explosion explosion = new Explosion(num2, item4.Direction, item4.RayCastInput.GetHitPosition(item4.HitResults[num5].Item1), item4.HitResults[num5].Item2, explosionImpactPercentage, Converter.ConvertBox2DToWorld(item4.RayCastInput.p1), worldRadius, explosionDamage, ExplosionData.HitType.Damage);
						if (item2.IsPlayer && ((Player)item2.InternalData).IgnoreExplosionCountDamage >= explosionCount)
						{
							explosion.SourceExplosionDamage = 0f;
							explosion.HitType = ExplosionData.HitType.Shockwave;
						}
						ExplosionBeforeHitEventArgs e = new ExplosionBeforeHitEventArgs();
						item2.BeforeExplosionHit(explosion, e);
						if (!e.Cancel)
						{
							ExplosionHitEventArgs e2 = new ExplosionHitEventArgs();
							item2.ExplosionHit(explosion, e2);
							list.Add(new Pair<ObjectData, Explosion>(item2, explosion));
						}
					}
				}
				if (!item2.TerminationInitiated)
				{
					num4 = item4.HitResults[num5].Item1;
					break;
				}
			}
			list2.Add(new ExplosionData.ExplosionLine(item4.RayCastInput.p1, item4.RayCastInput.GetHitPosition(num4), num4));
		}
		Microsoft.Xna.Framework.Vector2 vector4 = Microsoft.Xna.Framework.Vector2.Zero;
		ExplosionData.ExplosionLine explosionLine = null;
		ExplosionData.ExplosionLine explosionLine2 = null;
		ExplosionData.ExplosionLine explosionLine3 = null;
		for (int num6 = list2.Count - 1; num6 >= 0; num6--)
		{
			if (explosionLine == null)
			{
				explosionLine = list2[num6];
			}
			else if (explosionLine2 == null)
			{
				explosionLine2 = list2[num6];
				vector4 = explosionLine2.RayCastInput.p2 - explosionLine.RayCastInput.p2;
				if (vector4.IsValid())
				{
					vector4.Normalize();
				}
			}
			else
			{
				explosionLine3 = list2[num6];
				Microsoft.Xna.Framework.Vector2 vector5 = explosionLine3.RayCastInput.p2 - explosionLine.RayCastInput.p2;
				if (vector5.IsValid())
				{
					vector5.Normalize();
				}
				if (((explosionLine3.OriginalFraction >= 0.999999f && explosionLine2.OriginalFraction >= 0.999999f && !(explosionLine.OriginalFraction < 0.999999f)) || (explosionLine3.OriginalFraction < 0.999999f && explosionLine2.OriginalFraction < 0.999999f && explosionLine.OriginalFraction < 0.999999f)) && Microsoft.Xna.Framework.Vector2.Dot(vector4, vector5) > ((explosionLine3.OriginalFraction >= 0.999999f) ? 0.996f : 0.999f))
				{
					list2.RemoveAt(num6 + 1);
					explosionLine2 = explosionLine3;
					explosionLine3 = null;
				}
				else
				{
					explosionLine = explosionLine2;
					explosionLine2 = explosionLine3;
					vector4 = explosionLine2.RayCastInput.p2 - explosionLine.RayCastInput.p2;
					if (vector4.IsValid())
					{
						vector4.Normalize();
					}
					explosionLine3 = null;
				}
			}
		}
		float[] array = new float[list2.Count];
		for (int num7 = 0; num7 < list2.Count; num7++)
		{
			array[num7] = (list2[num7].RayCastInput.p2 - list2[num7].RayCastInput.p1).LengthSquared();
		}
		for (int num8 = 0; num8 < list2.Count; num8++)
		{
			float num9 = array[num8];
			float num10 = array[(num8 == 0) ? (list2.Count - 1) : (num8 - 1)];
			float num11 = array[(num8 != list2.Count - 1) ? (num8 + 1) : 0];
			if (num9 > num10 || num9 > num11)
			{
				float num12 = num9 - num11;
				float num13 = num9 - num10;
				if (Math.Abs(Math.Abs(num12) - Math.Abs(num13)) > 0.13f)
				{
					Microsoft.Xna.Framework.Vector2 position = list2[num8].RayCastInput.p2;
					position -= list2[num8].RayCastInput.p1;
					SFDMath.RotatePosition(ref position, (num12 < num13) ? (-0.06f) : 0.06f, out position);
					Box2D.XNA.RayCastInput rayCastInput = list2[num8].RayCastInput;
					rayCastInput.p2 = position + list2[num8].RayCastInput.p1;
					list2[num8].RayCastInput = rayCastInput;
				}
			}
		}
		float num14 = 0.1f;
		for (int num15 = 0; num15 < list2.Count; num15++)
		{
			list2[num15].Fill(num14);
		}
		ExplosionData.ExplosionLine explosionLine4 = null;
		ExplosionData.ExplosionLine explosionLine5 = null;
		Box2D.XNA.RayCastInput rayCastInputToNext = default(Box2D.XNA.RayCastInput);
		list2.Sort((ExplosionData.ExplosionLine explosionLine6, ExplosionData.ExplosionLine explosionLine7) => explosionLine6.Angle.CompareTo(explosionLine7.Angle));
		for (int num16 = 0; num16 < list2.Count; num16++)
		{
			explosionLine4 = list2[num16];
			explosionLine5 = list2[(num16 != list2.Count - 1) ? (num16 + 1) : 0];
			rayCastInputToNext.p1 = explosionLine4.RayCastInput.p2;
			rayCastInputToNext.p2 = explosionLine5.RayCastInput.p2;
			rayCastInputToNext.maxFraction = 1f;
			explosionLine4.RayCastInputToNext = rayCastInputToNext;
		}
		explosionLine4 = null;
		explosionLine5 = null;
		ExplosionData explosionData = new ExplosionData(num2, list2, vector, num + num14, explosionDamage);
		m_recentExplosions.Add(explosionData);
		explosionData.AffectedObjects.AddRange(list);
		list.Clear();
		int num17 = 0;
		bool flag = false;
		foreach (ObjectData item5 in objectDataToDamage)
		{
			if (item5.Type == Tile.TYPE.Player)
			{
				flag = !((Player)item5.InternalData).IsDead;
			}
			if (explosionData.AffectObject(item5, explosionCount) && item5.Type == Tile.TYPE.Player && flag)
			{
				num17++;
			}
		}
		if (GameOwner != GameOwnerEnum.Client && num17 >= 2)
		{
			SlowmotionHandler.AddSlowmotion(new Slowmotion(100f, 2000f, 200f, 0.2f, 0));
		}
		foreach (ExplosionData.ExplosionLineBuilder item6 in inputs)
		{
			item6.HitResults.Clear();
		}
		inputs.Clear();
		inputs = null;
		objectDataToDamage.Clear();
		objectDataToDamage = null;
	}

	public void TriggerFireplosion(Microsoft.Xna.Framework.Vector2 worldPosition, float worldRadius)
	{
		if (GameOwner != GameOwnerEnum.Server)
		{
			SoundHandler.PlaySound("Flamethrower", worldPosition, this);
		}
		if (GameOwner == GameOwnerEnum.Client)
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertWorldToBox2D(worldPosition);
		float num = Converter.ConvertWorldToBox2D(worldRadius);
		Microsoft.Xna.Framework.Vector2 vector2 = new Microsoft.Xna.Framework.Vector2(num, num);
		AABB aabb = default(AABB);
		aabb.lowerBound = vector - vector2;
		aabb.upperBound = vector + vector2;
		aabb.Grow(0.02f);
		List<ExplosionData.ExplosionLine> list = new List<ExplosionData.ExplosionLine>();
		List<ExplosionData.ExplosionLineBuilder> inputs = new List<ExplosionData.ExplosionLineBuilder>(20);
		for (int i = 0; i < 20; i++)
		{
			float num2 = (float)Math.PI / 10f * (float)i + (float)Math.PI / 20f;
			Microsoft.Xna.Framework.Vector2 vector3 = new Microsoft.Xna.Framework.Vector2((float)Math.Cos(num2), (float)Math.Sin(num2));
			ExplosionData.ExplosionLineBuilder item = new ExplosionData.ExplosionLineBuilder(vector3, new Box2D.XNA.RayCastInput
			{
				p1 = vector,
				maxFraction = 1f,
				p2 = vector + vector3 * num
			});
			inputs.Add(item);
		}
		HashSet<ObjectData> objectDataToDamage = new HashSet<ObjectData>();
		Filter filter;
		GetActiveWorld.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture != null && fixture.GetUserData() != null && !fixture.IsCloud())
			{
				fixture.GetFilterData(out filter);
				ObjectData objectData = ObjectData.Read(fixture);
				bool flag = objectData.Tile.Material.Resistance.Fire.Modifier > 0f;
				if (filter.blockFire && !objectData.TerminationInitiated)
				{
					for (int j = 0; j < inputs.Count; j++)
					{
						ExplosionData.ExplosionLineBuilder explosionLineBuilder = inputs[j];
						if (fixture.RayCast(out var output, ref explosionLineBuilder.RayCastInput))
						{
							explosionLineBuilder.HitResults.Add(new ItemContainer<float, Fixture, ObjectData>(output.fraction, fixture, objectData));
						}
					}
				}
				if (flag && !objectDataToDamage.Contains(objectData))
				{
					objectDataToDamage.Add(objectData);
				}
			}
			return true;
		}, ref aabb);
		foreach (ExplosionData.ExplosionLineBuilder item3 in inputs)
		{
			item3.SortHits();
		}
		foreach (ExplosionData.ExplosionLineBuilder item4 in inputs)
		{
			float num3 = 1f;
			for (int num4 = 0; num4 < item4.HitResults.Count; num4++)
			{
				ObjectData item2 = item4.HitResults[num4].Item3;
				if (!item2.TerminationInitiated)
				{
					if (objectDataToDamage.Contains(item2))
					{
						objectDataToDamage.Remove(item2);
						item2.SetMaxFire();
					}
					if (!item2.TerminationInitiated)
					{
						num3 = item4.HitResults[num4].Item1;
						break;
					}
				}
			}
			list.Add(new ExplosionData.ExplosionLine(item4.RayCastInput.p1, item4.RayCastInput.GetHitPosition(num3), num3));
		}
		Microsoft.Xna.Framework.Vector2 vector4 = Microsoft.Xna.Framework.Vector2.Zero;
		ExplosionData.ExplosionLine explosionLine = null;
		ExplosionData.ExplosionLine explosionLine2 = null;
		ExplosionData.ExplosionLine explosionLine3 = null;
		for (int num5 = list.Count - 1; num5 >= 0; num5--)
		{
			if (explosionLine == null)
			{
				explosionLine = list[num5];
			}
			else if (explosionLine2 == null)
			{
				explosionLine2 = list[num5];
				vector4 = explosionLine2.RayCastInput.p2 - explosionLine.RayCastInput.p2;
				if (vector4.IsValid())
				{
					vector4.Normalize();
				}
			}
			else
			{
				explosionLine3 = list[num5];
				Microsoft.Xna.Framework.Vector2 vector5 = explosionLine3.RayCastInput.p2 - explosionLine.RayCastInput.p2;
				if (vector5.IsValid())
				{
					vector5.Normalize();
				}
				if (((explosionLine3.OriginalFraction >= 0.999999f && explosionLine2.OriginalFraction >= 0.999999f && !(explosionLine.OriginalFraction < 0.999999f)) || (explosionLine3.OriginalFraction < 0.999999f && explosionLine2.OriginalFraction < 0.999999f && explosionLine.OriginalFraction < 0.999999f)) && Microsoft.Xna.Framework.Vector2.Dot(vector4, vector5) > ((explosionLine3.OriginalFraction >= 0.999999f) ? 0.996f : 0.999f))
				{
					list.RemoveAt(num5 + 1);
					explosionLine2 = explosionLine3;
					explosionLine3 = null;
				}
				else
				{
					explosionLine = explosionLine2;
					explosionLine2 = explosionLine3;
					vector4 = explosionLine2.RayCastInput.p2 - explosionLine.RayCastInput.p2;
					if (vector4.IsValid())
					{
						vector4.Normalize();
					}
					explosionLine3 = null;
				}
			}
		}
		float[] array = new float[list.Count];
		for (int num6 = 0; num6 < list.Count; num6++)
		{
			array[num6] = (list[num6].RayCastInput.p2 - list[num6].RayCastInput.p1).LengthSquared();
		}
		for (int num7 = 0; num7 < list.Count; num7++)
		{
			float num8 = array[num7];
			float num9 = array[(num7 == 0) ? (list.Count - 1) : (num7 - 1)];
			float num10 = array[(num7 != list.Count - 1) ? (num7 + 1) : 0];
			if (num8 > num9 || num8 > num10)
			{
				float num11 = num8 - num10;
				float num12 = num8 - num9;
				if (Math.Abs(Math.Abs(num11) - Math.Abs(num12)) > 0.13f)
				{
					Microsoft.Xna.Framework.Vector2 position = list[num7].RayCastInput.p2;
					position -= list[num7].RayCastInput.p1;
					SFDMath.RotatePosition(ref position, (num11 < num12) ? (-0.06f) : 0.06f, out position);
					Box2D.XNA.RayCastInput rayCastInput = list[num7].RayCastInput;
					rayCastInput.p2 = position + list[num7].RayCastInput.p1;
					list[num7].RayCastInput = rayCastInput;
				}
			}
		}
		float num13 = 0.1f;
		for (int num14 = 0; num14 < list.Count; num14++)
		{
			list[num14].Fill(num13);
		}
		ExplosionData.ExplosionLine explosionLine4 = null;
		ExplosionData.ExplosionLine explosionLine5 = null;
		Box2D.XNA.RayCastInput rayCastInputToNext = default(Box2D.XNA.RayCastInput);
		list.Sort((ExplosionData.ExplosionLine e1, ExplosionData.ExplosionLine e2) => e1.Angle.CompareTo(e2.Angle));
		for (int num15 = 0; num15 < list.Count; num15++)
		{
			explosionLine4 = list[num15];
			explosionLine5 = list[(num15 != list.Count - 1) ? (num15 + 1) : 0];
			rayCastInputToNext.p1 = explosionLine4.RayCastInput.p2;
			rayCastInputToNext.p2 = explosionLine5.RayCastInput.p2;
			rayCastInputToNext.maxFraction = 1f;
			explosionLine4.RayCastInputToNext = rayCastInputToNext;
		}
		explosionLine4 = null;
		explosionLine5 = null;
		ExplosionData explosionData = new ExplosionData(++m_fireplosionDataInstanceID, list, vector, num + num13, 0f);
		foreach (ObjectData item5 in objectDataToDamage)
		{
			explosionData.AffectObjectFire(item5);
		}
		foreach (ExplosionData.ExplosionLineBuilder item6 in inputs)
		{
			item6.HitResults.Clear();
		}
		inputs.Clear();
		inputs = null;
		objectDataToDamage.Clear();
		objectDataToDamage = null;
	}

	public void CreateFireGrid()
	{
		FireGrid = new FireGrid(this);
	}

	public void DisposeFireGrid()
	{
		if (FireGrid != null)
		{
			FireGrid.Dispose();
			FireGrid = null;
		}
		if (m_createdFireNodes != null)
		{
			m_createdFireNodes.Clear();
			m_createdFireNodes = null;
		}
		if (m_removedFireNodes != null)
		{
			m_removedFireNodes.Clear();
			m_removedFireNodes = null;
		}
	}

	public void HandleFireObjectUpdate(ref NetMessage.FireObjectUpdate.Data data)
	{
		for (int i = 0; i < data.FireObjectValues.Count; i++)
		{
			NetMessage.FireObjectUpdate.FireValues fireValues = data.FireObjectValues[i];
			ObjectData objectDataByID = GetObjectDataByID(fireValues.ObjectID);
			if (objectDataByID != null)
			{
				objectDataByID.Fire.IgnitionValue = fireValues.IgnitionValue;
				objectDataByID.Fire.SmokeTime = fireValues.SmokeTime;
				objectDataByID.Fire.BurnTime = fireValues.BurnTime;
				objectDataByID.StartTrackingFireValues();
			}
		}
	}

	public void HandleFireRemoveUpdate(ref NetMessage.FireRemoveUpdate.Data data)
	{
		SFD.Fire.FireNode fireNode = null;
		int num = 0;
		for (int i = 0; i < data.RemovedFireNodes.Count; i++)
		{
			num = data.RemovedFireNodes[i];
			if (!m_removedFireNodes.Contains(num))
			{
				m_removedFireNodes.Add(num);
			}
			if (FireGrid.ActiveFireNodes.ContainsKey(num))
			{
				fireNode = FireGrid.ActiveFireNodes[num];
				FireGrid.ActiveFireNodes.Remove(fireNode.FireID);
				int num2 = (int)(fireNode.Position.X / 2.56f) - ((fireNode.Position.X < 0f) ? 1 : 0);
				int num3 = (int)(fireNode.Position.Y / 2.56f) - ((fireNode.Position.Y < 0f) ? 1 : 0);
				int key = num2 * 100000 + num3;
				FireSquare fireSquare = FireGrid.FireSquares[key];
				fireSquare.FireNodes.Remove(fireNode);
				fireNode.Removed = true;
				if (fireSquare.FireNodes.Count <= 0 && fireSquare.TrackingObjects.Count <= 0 && fireSquare.TransitionFireNodes.Count <= 0)
				{
					fireSquare.IsActive = false;
					FireGrid.ActiveFireSquares.Remove(fireSquare);
				}
			}
		}
		for (int j = 0; j < data.RemovedFireObjects.Count; j++)
		{
			num = data.RemovedFireObjects[j];
			ObjectData objectDataByID = GetObjectDataByID(num);
			if (objectDataByID != null)
			{
				objectDataByID.Fire.BurnTime = 0f;
				objectDataByID.Fire.IgnitionValue = 0f;
				objectDataByID.Fire.SmokeTime = 0f;
			}
		}
	}

	public void HandleFireUpdate(ref NetMessage.FireUpdate.Data[] data, int count)
	{
		SFD.Fire.FireNode fireNode = null;
		NetMessage.FireUpdate.Data data2 = null;
		BodyData bodyData = null;
		for (int i = 0; i < count; i++)
		{
			data2 = data[i];
			if (!FireGrid.ActiveFireNodes.ContainsKey(data2.FireID))
			{
				if (!m_removedFireNodes.Contains(data2.FireID))
				{
					fireNode = FireGrid.AddFireNode(Microsoft.Xna.Framework.Vector2.Zero, data2.Position, data2.StartPosition, data2.Velocity, data2.Type, data2.FireID);
					fireNode.PlayerOwnerId = data2.PlayerOwnerID;
					m_createdFireNodes.Add(fireNode.FireID);
					if (fireNode.PlayerOwnerId != 0 && fireNode.Type == FireNodeTypeEnum.Flamethrower)
					{
						Player player = GetPlayer(fireNode.PlayerOwnerId);
						if (player != null)
						{
							player.ShowRangedWeaponFireRecoil();
							if (!BringPlayerToFront.Contains(player))
							{
								BringPlayerToFront.Add(player);
							}
						}
					}
				}
				else
				{
					fireNode = null;
				}
			}
			else
			{
				fireNode = FireGrid.ActiveFireNodes[data2.FireID];
				if (!fireNode.MessageIsNewer(data2.MessageCount, TAS: true))
				{
					fireNode = null;
				}
			}
			if (fireNode == null)
			{
				continue;
			}
			int num = (int)(fireNode.Position.X / 2.56f) - ((fireNode.Position.X < 0f) ? 1 : 0);
			int num2 = (int)(fireNode.Position.Y / 2.56f) - ((fireNode.Position.Y < 0f) ? 1 : 0);
			int key = num * 100000 + num2;
			FireSquare fireSquare = FireGrid.FireSquares[key];
			fireNode.SlowingDown = data2.SlowingDown;
			fireNode.StartPosition = data2.StartPosition;
			fireNode.Velocity = data2.Velocity;
			fireNode.CheckTunneling = false;
			fireNode.PlayerOwnerId = data2.PlayerOwnerID;
			fireNode.IgnoreBodyId = data2.IgnoreBodyID;
			fireNode.Lifetime = data2.Lifetime - 250f;
			fireNode.MessageCountReceive = data2.MessageCount;
			bodyData = ((data2.OwnerBodyID == 0) ? null : GetBodyDataByID(data2.OwnerBodyID));
			if (bodyData != null)
			{
				fireNode.OwnerObject = bodyData.Object;
				fireNode.OwnerBody = bodyData.Owner;
				fireNode.OwnerBodyPosition = data2.Position;
				fireNode.Position = fireNode.OwnerBody.GetWorldPoint(fireNode.OwnerBodyPosition);
			}
			else
			{
				fireNode.OwnerObject = null;
				fireNode.OwnerBody = null;
				fireNode.Position = data2.Position;
			}
			fireNode.OwnerBodyLocalNormal = data2.OwnerBodyLocalNormal;
			if (fireNode.CloudsToIgnore != data2.CloudsToIgnore)
			{
				fireNode.CloudsToIgnore = data2.CloudsToIgnore;
				fireNode.CloudsToIgnoreTime = 0f;
			}
			num = (int)(fireNode.Position.X / 2.56f) - ((fireNode.Position.X < 0f) ? 1 : 0);
			num2 = (int)(fireNode.Position.Y / 2.56f) - ((fireNode.Position.Y < 0f) ? 1 : 0);
			key = num * 100000 + num2;
			FireSquare fireSquare2 = null;
			if (!FireGrid.FireSquares.ContainsKey(key))
			{
				FireGrid.FireSquares.Add(key, new FireSquare(this, num, num2));
			}
			fireSquare2 = FireGrid.FireSquares[key];
			if (fireSquare != fireSquare2)
			{
				fireSquare.FireNodes.Remove(fireNode);
				if (fireSquare.FireNodes.Count <= 0 && fireSquare.TrackingObjects.Count <= 0 && fireSquare.TransitionFireNodes.Count <= 0)
				{
					fireSquare.IsActive = false;
					FireGrid.ActiveFireSquares.Remove(fireSquare);
				}
				fireSquare2.FireNodes.Add(fireNode);
				if (!fireSquare2.IsActive)
				{
					fireSquare2.IsActive = true;
					FireGrid.ActiveFireSquares.Add(fireSquare2);
				}
			}
		}
	}

	public void InitializeGibbingData()
	{
		m_objectImpulsePointsPool = new GenericClassPool<ObjectImpulsePoints>(ObjectImpulsePoints.CreateNew);
		m_objectImpulsePointsList = new List<ObjectImpulsePoints>(128);
		m_objectImpulsePointsDictionary = new Dictionary<ObjectData, ObjectImpulsePoints>(128);
		m_gibbingObjectGroups = new ObjectGibbGroups();
	}

	public void DisposeGibbingData()
	{
		if (m_gibbingObjectGroups != null)
		{
			m_gibbingObjectGroups.Dispose();
			m_gibbingObjectGroups = null;
		}
		if (m_objectImpulsePointsPool != null)
		{
			m_objectImpulsePointsPool.Clear();
			m_objectImpulsePointsPool = null;
		}
		if (m_objectImpulsePointsList != null)
		{
			for (int i = 0; i < m_objectImpulsePointsList.Count; i++)
			{
				m_objectImpulsePointsList[i].ImpulsePoints.Clear();
				m_objectImpulsePointsList[i].ImpulsePoints = null;
			}
			m_objectImpulsePointsList.Clear();
			m_objectImpulsePointsList = null;
		}
		if (m_objectImpulsePointsDictionary != null)
		{
			m_objectImpulsePointsDictionary.Clear();
			m_objectImpulsePointsDictionary = null;
		}
	}

	public void RegisterImpulsePoint(ObjectData od, Fixture fixture, ObjectData otherOd, Fixture otherFixture, Microsoft.Xna.Framework.Vector2 worldNormal, Microsoft.Xna.Framework.Vector2 worldPoint, float impulse)
	{
		if (impulse <= 0f)
		{
			return;
		}
		impulse *= m_gibbingImpulseSlowmotionModifier * 4.5f;
		impulse *= 0.5f;
		if (impulse < 0.005f)
		{
			impulse = 0.005f;
		}
		ObjectImpulsePoints objectImpulsePoints = null;
		Body body = fixture.GetBody();
		Microsoft.Xna.Framework.Vector2 zero = Microsoft.Xna.Framework.Vector2.Zero;
		Microsoft.Xna.Framework.Vector2 zero2 = Microsoft.Xna.Framework.Vector2.Zero;
		if (od.Tile.GibPreassureSpike > 0f || od.Tile.GibPreassureTotal > 0f)
		{
			zero = body.GetLocalPoint(worldPoint);
			zero2 = body.GetLocalVector(worldNormal);
			if (m_objectImpulsePointsDictionary.ContainsKey(od))
			{
				objectImpulsePoints = m_objectImpulsePointsDictionary[od];
			}
			else
			{
				objectImpulsePoints = m_objectImpulsePointsPool.GetFreeItem();
				m_objectImpulsePointsDictionary.Add(od, objectImpulsePoints);
				m_objectImpulsePointsList.Add(objectImpulsePoints);
				objectImpulsePoints.ObjectData = od;
				objectImpulsePoints.Body = body;
			}
			ImpulsePoint item = default(ImpulsePoint);
			item.Clear();
			item.OtherObject = otherOd;
			item.OtherFixture = otherFixture;
			item.Fixture = fixture;
			item.Impulse = impulse;
			item.WorldNormal = worldNormal;
			item.WorldPoint = worldPoint;
			item.LocalNormal = zero2;
			item.LocalPoint = zero;
			objectImpulsePoints.ImpulsePoints.Add(item);
		}
		impulse *= 0.5f;
		List<Body> connectedWeldedBodies = body.GetConnectedWeldedBodies();
		if (connectedWeldedBodies == null)
		{
			return;
		}
		for (int i = 0; i < connectedWeldedBodies.Count; i++)
		{
			body = connectedWeldedBodies[i];
			fixture = body.GetFixtureList();
			if (fixture == null)
			{
				continue;
			}
			od = ObjectData.Read(fixture);
			if (od.Tile.GibPreassureSpike > 0f || od.Tile.GibPreassureTotal > 0f)
			{
				if (m_objectImpulsePointsDictionary.ContainsKey(od))
				{
					objectImpulsePoints = m_objectImpulsePointsDictionary[od];
				}
				else
				{
					objectImpulsePoints = m_objectImpulsePointsPool.GetFreeItem();
					m_objectImpulsePointsDictionary.Add(od, objectImpulsePoints);
					m_objectImpulsePointsList.Add(objectImpulsePoints);
					objectImpulsePoints.ObjectData = od;
					objectImpulsePoints.Body = body;
				}
				worldPoint = body.GetPosition();
				zero = body.GetLocalPoint(worldPoint);
				zero2 = body.GetLocalVector(worldNormal);
				ImpulsePoint item = default(ImpulsePoint);
				item.Clear();
				item.OtherObject = od;
				item.OtherFixture = otherFixture;
				item.Fixture = fixture;
				item.Impulse = impulse;
				item.WorldNormal = worldNormal;
				item.WorldPoint = worldPoint;
				item.LocalNormal = zero2;
				item.LocalPoint = zero;
				objectImpulsePoints.ImpulsePoints.Add(item);
			}
		}
	}

	public void HandleImpulsePoints()
	{
		m_gibbingCurrentFrameCounter++;
		ObjectImpulsePoints objectImpulsePoints = null;
		m_gibbingObjectGroups.Clear();
		for (int num = m_objectImpulsePointsList.Count - 1; num >= 0; num--)
		{
			objectImpulsePoints = m_objectImpulsePointsList[num];
			HandleImpulsePoints(objectImpulsePoints.ObjectData, objectImpulsePoints.Body, objectImpulsePoints.ImpulsePoints);
			objectImpulsePoints.ImpulsePoints.Clear();
			m_objectImpulsePointsPool.FlagFreeItem(m_objectImpulsePointsList[num]);
			m_objectImpulsePointsDictionary.Remove(m_objectImpulsePointsList[num].ObjectData);
			m_objectImpulsePointsList.RemoveAt(num);
		}
		m_gibbingObjectGroups.HandleGibbGroups();
	}

	public void HandleImpulsePoints(ObjectData objectData, Body body, List<ImpulsePoint> impulsePoints)
	{
		float num = 0f;
		float num2 = 0f;
		float num3 = 0f;
		bool flag = true;
		bool flag2 = false;
		float num4 = 0f;
		m_gibForces.Clear();
		if (objectData.IsPlayer && impulsePoints.Count > 0)
		{
			Player player = (Player)objectData.InternalData;
			if (!player.IsRemoved && player.StandingOnBody != null && player.StandingOnGround)
			{
				Microsoft.Xna.Framework.Vector2 walkNormal = Microsoft.Xna.Framework.Vector2.UnitY;
				Fixture walkFixture = null;
				if (!player.Contacts.GetWalkNormal(ref player.CurrentSpeed, out walkNormal, out walkFixture))
				{
					walkNormal = Microsoft.Xna.Framework.Vector2.UnitY;
					walkFixture = player.StandingOnBody.GetFixtureList();
				}
				ImpulsePoint item = default(ImpulsePoint);
				item.Clear();
				item.Fixture = player.GetFixtureCircle();
				item.Impulse = Converter.ConvertMassKGToBox2D(80f);
				item.OtherFixture = walkFixture;
				item.OtherObject = ObjectData.Read(walkFixture);
				item.WorldPoint = player.WorldBody.GetPosition();
				item.LocalPoint = Microsoft.Xna.Framework.Vector2.Zero;
				item.LocalNormal = walkNormal;
				item.WorldNormal = walkNormal;
				impulsePoints.Add(item);
			}
		}
		for (int i = 0; i < impulsePoints.Count; i++)
		{
			ImpulsePoint impulsePoint = impulsePoints[i];
			Microsoft.Xna.Framework.Vector2 localNormal = impulsePoint.LocalNormal;
			if (objectData.Tile.GibPreassureEnableOneWay)
			{
				num4 = -1f;
				num3 = impulsePoint.Impulse;
				if (m_gibForces.ContainsKey(impulsePoint.OtherFixture))
				{
					num3 = Math.Max(num3, m_gibForces[impulsePoint.OtherFixture]);
					num2 -= m_gibForces[impulsePoint.OtherFixture];
					num2 += num3;
					num -= m_gibForces[impulsePoint.OtherFixture];
					num += num3;
					m_gibForces[impulsePoint.OtherFixture] = num3;
				}
				else
				{
					num2 += num3;
					num += num3;
					m_gibForces.Add(impulsePoint.OtherFixture, num3);
				}
				continue;
			}
			for (int j = i + 1; j < impulsePoints.Count; j++)
			{
				ImpulsePoint impulsePoint2 = impulsePoints[j];
				Microsoft.Xna.Framework.Vector2 localNormal2 = impulsePoint2.LocalNormal;
				if (impulsePoint.OtherFixture.GetBody().GetType() == Box2D.XNA.BodyType.Static && impulsePoint2.OtherFixture.GetBody().GetType() == Box2D.XNA.BodyType.Static)
				{
					continue;
				}
				num4 = Microsoft.Xna.Framework.Vector2.Dot(localNormal, localNormal2);
				if (!(num4 < 0f))
				{
					continue;
				}
				if (objectData.IsPlayer)
				{
					if (impulsePoint.Fixture.TileFixtureIndex != 0 || !(num4 < -0.3f))
					{
						continue;
					}
					num4 = -1f;
					List<Body> connectedWeldedBodies = impulsePoint.OtherFixture.GetBody().GetConnectedWeldedBodies();
					Body body2 = impulsePoint.OtherFixture.GetBody();
					float num5 = body2.GetMass();
					if (connectedWeldedBodies != null)
					{
						for (int k = 0; k < connectedWeldedBodies.Count; k++)
						{
							num5 += connectedWeldedBodies[k].GetMass();
						}
					}
					flag = Converter.ConvertMassBox2DToKG(num5) >= 99f;
					flag2 = flag2 || flag;
					if (impulsePoint.OtherObject.InstaGibPlayer && body2.GetType() == Box2D.XNA.BodyType.Dynamic && num4 < -0.3f && Microsoft.Xna.Framework.Vector2.Dot(impulsePoint.LocalNormal, -Microsoft.Xna.Framework.Vector2.UnitY) > 0.6f)
					{
						objectData.Destroy();
					}
				}
				num3 = (impulsePoint.Impulse + (impulsePoint2.Impulse - impulsePoint.Impulse) * 0.5f) * (0f - num4);
				if (m_gibForces.ContainsKey(impulsePoint.OtherFixture))
				{
					num3 = Math.Max(num3, m_gibForces[impulsePoint.OtherFixture]);
					num2 -= m_gibForces[impulsePoint.OtherFixture];
					num2 += num3;
					if (flag)
					{
						num -= m_gibForces[impulsePoint.OtherFixture];
						num += num3;
					}
					m_gibForces[impulsePoint.OtherFixture] = num3;
				}
				else
				{
					num2 += num3;
					if (flag)
					{
						num += num3;
					}
					m_gibForces.Add(impulsePoint.OtherFixture, num3);
				}
			}
		}
		objectData.AddGibbFramePressure(num2, m_gibbingCurrentFrameCounter);
		if (objectData.Tile.GibPreassureSpike > 0f && num > objectData.Tile.GibPreassureSpike)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, $"Gib.SPIKE on object {objectData.ObjectID}");
			m_gibbingObjectGroups.AddGibbObject(objectData, body, num2, impulsePoints);
		}
		else if (objectData.Tile.GibPreassureTotal > 0f && objectData.MinimalGibbPressure > objectData.Tile.GibPreassureTotal)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, $"Gib.Preassure on object {objectData.ObjectID}");
			m_gibbingObjectGroups.AddGibbObject(objectData, body, num2, impulsePoints);
		}
		if (objectData.IsPlayer && flag2 && objectData.MinimalGibbPressureLast12Frames > 0f && Converter.ConvertMassBox2DToKG(objectData.MinimalGibbPressureLast12Frames) > Converter.ConvertMassBox2DToKG(objectData.Tile.GibPreassureTotal) * 0.5f)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, $"Gib.Preassure last 20 frames on player {objectData.ObjectID}");
			m_gibbingObjectGroups.AddGibbObject(objectData, body, num2, impulsePoints);
		}
	}

	public GroupInfo GetGroupInfo(ushort groupID)
	{
		GroupInfo value = null;
		if (GroupInfo.TryGetValue(groupID, out value))
		{
			return value;
		}
		return null;
	}

	public void AddToGroup(ObjectData od, ushort groupID)
	{
		GroupInfo value = null;
		if (!GroupInfo.TryGetValue(groupID, out value))
		{
			value = new GroupInfo();
			GroupInfo.Add(groupID, value);
		}
		if (od is ObjectGroupMarker)
		{
			value.Marker = (ObjectGroupMarker)od;
		}
		else if (!value.Objects.Contains(od))
		{
			value.Objects.Add(od);
		}
	}

	public void RemoveFromGroup(ObjectData od, ushort groupID)
	{
		GroupInfo value = null;
		if (GroupInfo.TryGetValue(groupID, out value))
		{
			if (od is ObjectGroupMarker)
			{
				value.Marker = null;
			}
			else
			{
				value.Objects.Remove(od);
			}
			if (value.Marker == null && value.Objects.Count == 0)
			{
				GroupInfo.Remove(groupID);
			}
		}
	}

	public void AddSpawnInfoToGroup(SpawnObjectInformation spawnObjectInformation)
	{
		GroupSpawnInfo value = null;
		if (!GroupSpawnInfo.TryGetValue(spawnObjectInformation.GroupID, out value))
		{
			value = new GroupSpawnInfo();
			GroupSpawnInfo.Add(spawnObjectInformation.GroupID, value);
		}
		if (spawnObjectInformation.ObjectData is ObjectGroupMarker)
		{
			value.Marker = spawnObjectInformation;
		}
		else
		{
			value.Objects.Add(spawnObjectInformation);
		}
	}

	public void PrepareSpawnGroups()
	{
		foreach (KeyValuePair<ushort, GroupSpawnInfo> item in GroupSpawnInfo)
		{
			if (item.Value.Marker != null)
			{
				ObjectGroupMarker objectGroupMarker = (ObjectGroupMarker)ObjectData.Read(CreateTile(item.Value.Marker));
				objectGroupMarker.SetGroupInfo(item.Value);
				if (EditMode)
				{
					objectGroupMarker.SpawnGroup(onLoadStart: true);
				}
			}
		}
	}

	public void SpawnGroupsOnStartup()
	{
		foreach (ObjectData item in GetObjectDataByMapObjectID("GroupMarker"))
		{
			if (!item.IsDisposed && !item.TerminationInitiated && item is ObjectGroupMarker)
			{
				ObjectGroupMarker objectGroupMarker = (ObjectGroupMarker)item;
				if (!objectGroupMarker.IsDisposed && objectGroupMarker.ActivateOnStartup)
				{
					objectGroupMarker.SpawnGroup(onLoadStart: true);
				}
			}
		}
	}

	public float GetStartupPlayerFlashTime()
	{
		if (!ObjectWorldData.StartupSequenceEnabled)
		{
			return 1250f;
		}
		return 2250f;
	}

	public void LoadGUI()
	{
		PlayerHUDs = default(FixedArray8B<PlayerHUD>);
		for (int i = 0; i < PlayerHUDs.Length; i++)
		{
			PlayerHUDs[i] = new PlayerHUD();
		}
		m_victoryText = new VictoryText(m_game);
		m_voteCounter = new VoteCounter(m_game);
		m_victoryText.Reset();
		m_voteCounter.Reset();
		m_startupText = new StartupSequence(m_game);
		m_transitionRadius = 4f;
		m_transitionCircle = Textures.GetTexture("transition_circle");
		m_transitionScreenCenter = new Microsoft.Xna.Framework.Point((int)GameSFD.GAME_WIDTH2f, (int)GameSFD.GAME_HEIGHT2f);
		m_deathText = LanguageHelper.GetText("statusText.youAreDead");
		m_deathTextSize = Constants.MeasureString(Constants.Font1, m_deathText);
		m_deathTextPosition = new Microsoft.Xna.Framework.Vector2(GameSFD.GAME_WIDTH2f - m_deathTextSize.X * 0.5f, (float)GameSFD.GAME_HEIGHT * (5f / 6f));
		m_spectatingText = LanguageHelper.GetText("statusText.spectating");
		m_spectatingTextSize = Constants.MeasureString(Constants.Font1, m_spectatingText);
		m_spectatingTextPosition = new Microsoft.Xna.Framework.Vector2(GameSFD.GAME_WIDTH2f - m_spectatingTextSize.X * 0.5f, (float)GameSFD.GAME_HEIGHT * (5f / 6f));
		m_spectatingTipText = LanguageHelper.GetText("statusText.spectatingTip");
		m_spectatingTipTextSize = Constants.MeasureString(Constants.Font1, m_spectatingTipText);
		m_spectatingTipTextPosition = new Microsoft.Xna.Framework.Vector2(GameSFD.GAME_WIDTH2f - m_spectatingTipTextSize.X * 0.5f, GameSFD.GAME_HEIGHT2f * 1.6f);
	}

	public void SetStartupFlash()
	{
		ActionOnAllLocalPlayers(delegate(Player x)
		{
			x.BeginFlash(GetStartupPlayerFlashTime());
		});
	}

	public void CalculateTransitionCicle()
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			lock (Client.ClientUpdateLockObject)
			{
				CalculateCameraArea();
			}
		}
		else
		{
			CalculateCameraArea();
		}
		Camera.PrepareConvertBox2DToScreenValues();
		Player primaryLocalPlayer = PrimaryLocalPlayer;
		if (primaryLocalPlayer != null && !primaryLocalPlayer.IsDisposed)
		{
			Microsoft.Xna.Framework.Vector2 worldCoordinate = primaryLocalPlayer.Position;
			Camera.ConvertWorldToScreen(ref worldCoordinate, out worldCoordinate);
			m_transitionScreenCenter = new Microsoft.Xna.Framework.Point((int)worldCoordinate.X, (int)worldCoordinate.Y);
		}
		m_showTransition = ObjectWorldData.StartupIrisSwipeEnabled;
	}

	public bool CheckLocalGameUserSpectating()
	{
		GameUser gameUser = LocalGameUsers[0];
		if (GameSFD.Handle.CurrentState == State.EditorTestRun && GameInfo.PrimaryLocalUserIndex >= 0)
		{
			gameUser = LocalGameUsers[GameInfo.PrimaryLocalUserIndex];
		}
		return gameUser?.SpectatingWhileWaitingToPlay ?? false;
	}

	public bool CheckAllLocalPlayersDead(bool spectatingLocalUserIsConsideredAlive = true)
	{
		if (PrimaryLocalPlayer != null && !PrimaryLocalPlayer.IsDisposed && !PrimaryLocalPlayer.IsDead)
		{
			return false;
		}
		bool result = false;
		int num = 0;
		while (true)
		{
			if (num < LocalPlayers.Length)
			{
				GameUser gameUser = LocalGameUsers[num];
				if (gameUser != null && !gameUser.IsDisposed)
				{
					if (gameUser.SpectatingWhileWaitingToPlay && spectatingLocalUserIsConsideredAlive)
					{
						return false;
					}
					result = true;
					Player player = LocalPlayers[num];
					if (player != null && !player.IsDisposed && !player.IsDead)
					{
						break;
					}
				}
				num++;
				continue;
			}
			return result;
		}
		return false;
	}

	public void SetLocalPlayers()
	{
		FixedArray8B<GameUser> localGameUsers = GameInfo.GetLocalGameUsers();
		for (int i = 0; i < LocalPlayers.Length; i++)
		{
			GameUser gameUser = localGameUsers[i];
			Player player = ((gameUser != null) ? GetPlayer(gameUser.LastPlayerID) : null);
			LocalGameUsers[i] = gameUser;
			LocalPlayers[i] = player;
			if (gameUser != null && gameUser.LastPlayerID != 0)
			{
				if (player != null && !player.IsDisposed)
				{
					LocalPlayerStatus[i] = ((!player.IsDead) ? PlayerStatus.Alive : PlayerStatus.Dead);
				}
				else
				{
					LocalPlayerStatus[i] = PlayerStatus.Removed;
				}
			}
			else
			{
				LocalPlayerStatus[i] = PlayerStatus.None;
			}
		}
		if (EditTestMode)
		{
			GameUser gameUser2 = localGameUsers[GameInfo.PrimaryLocalUserIndex];
			PrimaryLocalPlayer = ((gameUser2 != null) ? GetPlayer(gameUser2.LastPlayerID) : null);
		}
		else
		{
			PrimaryLocalPlayer = LocalPlayers[0];
		}
	}

	public void ActionOnAllLocalPlayers(Action<Player> callback)
	{
		for (int i = 0; i < LocalPlayers.Length; i++)
		{
			Player player = LocalPlayers[i];
			if (player != null && !player.IsDisposed)
			{
				callback(player);
			}
		}
	}

	public void UpdateLocalPlayersAimInput()
	{
		for (int i = 0; i < LocalPlayers.Length; i++)
		{
			Player player = LocalPlayers[i];
			if (player != null && !player.IsDisposed)
			{
				player.UpdateAimInput();
			}
		}
	}

	public void UpdateGUI(float ms)
	{
		if (!m_startupText.IsDone)
		{
			m_startupText.Update(this);
		}
	}

	public void DrawGUI(float ms)
	{
		DrawPrimaryTimer(ms);
		if (m_showTransition)
		{
			CalculateTransitionCicle();
			if (m_transitionRadius == 0f)
			{
				m_spriteBatch.Draw(Constants.WhitePixel, new Rectangle(0, 0, GameSFD.GAME_WIDTH, GameSFD.GAME_HEIGHT), Microsoft.Xna.Framework.Color.Black);
			}
			else
			{
				m_transitionRadius += ms * (GameSFD.GAME_WIDTHf / 2000f);
				int x = m_transitionScreenCenter.X;
				int y = m_transitionScreenCenter.Y;
				int num = (int)m_transitionRadius;
				int num2 = num * 2;
				Rectangle destinationRectangle = new Rectangle(x - num, y - num, num2, num2);
				m_spriteBatch.Draw(m_transitionCircle, destinationRectangle, Microsoft.Xna.Framework.Color.White);
				destinationRectangle = new Rectangle(0, 0, x - num, GameSFD.GAME_HEIGHT);
				if (destinationRectangle.Width > 0)
				{
					m_spriteBatch.Draw(Constants.WhitePixel, destinationRectangle, Microsoft.Xna.Framework.Color.Black);
				}
				destinationRectangle = new Rectangle(0, 0, GameSFD.GAME_WIDTH, y - num);
				if (destinationRectangle.Height > 0)
				{
					m_spriteBatch.Draw(Constants.WhitePixel, destinationRectangle, Microsoft.Xna.Framework.Color.Black);
				}
				destinationRectangle = new Rectangle(x + num, 0, GameSFD.GAME_WIDTH - x - num, GameSFD.GAME_HEIGHT);
				if (destinationRectangle.Width > 0)
				{
					m_spriteBatch.Draw(Constants.WhitePixel, destinationRectangle, Microsoft.Xna.Framework.Color.Black);
				}
				destinationRectangle = new Rectangle(0, y + num, GameSFD.GAME_WIDTH, GameSFD.GAME_HEIGHT - y - num);
				if (destinationRectangle.Height > 0)
				{
					m_spriteBatch.Draw(Constants.WhitePixel, destinationRectangle, Microsoft.Xna.Framework.Color.Black);
				}
				if (m_transitionRadius >= (float)GameSFD.GAME_WIDTH)
				{
					m_showTransition = false;
					m_transitionRadius = 0f;
				}
			}
		}
		if (!m_startupText.IsDone)
		{
			m_startupText.Draw(m_spriteBatch, ms);
		}
		bool flag;
		if ((flag = WorkingCameraMode == CameraMode.Individual && SpectatingPlayer != null && !SpectatingPlayer.IsLocal) && !GameOverData.IsOver)
		{
			Constants.DrawString(m_spriteBatch, Constants.Font1, m_currentlySpectatingText, m_currentlySpectatingTextPosition, Microsoft.Xna.Framework.Color.White, 0f, Microsoft.Xna.Framework.Vector2.Zero, 1f, SpriteEffects.None, 0);
			if (m_validPlayersToSpectate.Count >= 2)
			{
				Constants.DrawString(m_spriteBatch, Constants.Font1, m_spectatingTipText, m_spectatingTipTextPosition, Microsoft.Xna.Framework.Color.White, 0f, Microsoft.Xna.Framework.Vector2.Zero, 1f, SpriteEffects.None, 0);
			}
		}
		if (!GameOverData.IsOver)
		{
			if (!flag && CheckLocalGameUserSpectating())
			{
				if (GameSFD.GUIMode != ShowGUIMode.HideAll)
				{
					Constants.DrawString(m_spriteBatch, Constants.Font1, m_spectatingText, m_spectatingTextPosition, Microsoft.Xna.Framework.Color.White, 0f, Microsoft.Xna.Framework.Vector2.Zero, 1f, SpriteEffects.None, 0);
				}
			}
			else if (ObjectWorldData.DeathSequenceEnabled && CheckAllLocalPlayersDead())
			{
				if (m_deathTextBlinkTimer <= 0f)
				{
					m_deathTextBlink = !m_deathTextBlink;
					m_deathTextBlinkTimer += 125f;
				}
				m_deathTextBlinkTimer -= ms;
				if (!m_deathTextBlink && GameSFD.GUIMode != ShowGUIMode.HideAll && GameSFD.Saturation == 0f)
				{
					Constants.DrawString(m_spriteBatch, Constants.Font1, m_deathText, m_deathTextPosition, m_deathTextColor, 0f, Microsoft.Xna.Framework.Vector2.Zero, 1f, SpriteEffects.None, 0);
				}
			}
		}
		if (GameOverData.IsOver && (GameOwner != GameOwnerEnum.Local || GameOverSignalSent))
		{
			if (!m_gameOverInitialized)
			{
				SoundHandler.PlayGlobalSound("GameOver1");
				m_gameOverInitialized = true;
			}
			if (!GameInfo.VoteInfo.MapVoteInitiated)
			{
				m_voteCounter.Draw(m_spriteBatch, GameOverData.GameOverVotes, GameOverData.GameOverMaxVotes);
			}
			m_nextTimeUpdate -= ms;
			if (m_nextTimeUpdate <= 0f)
			{
				m_victoryText.UpdateTimerText(GameOverData.GameOverTimeLeft);
				m_nextTimeUpdate = 100f;
			}
			m_victoryText.Draw(m_spriteBatch, ms, this);
		}
		int slotIndex = 0;
		for (int i = 0; i < LocalPlayers.Length; i++)
		{
			PlayerHUDs[i].Draw(this, i, m_spriteBatch, ms, ref slotIndex);
		}
	}

	public void DrawPrimaryTimer(float ms)
	{
		GameTimer primaryTimer = PrimaryTimer;
		if (primaryTimer == null)
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 vector = new Microsoft.Xna.Framework.Vector2(GameSFD.GAME_WIDTH2f, ((float)PlayerHUD.GetTotalWidthOfAllPlayerHuds() >= GameSFD.GAME_WIDTH2f - 32f) ? ((float)PlayerHUD.GetHeight() + 32f) : 32f);
		Microsoft.Xna.Framework.Color color = (SuddenDeathActive ? Constants.COLORS.SUDDEN_DEATH : Microsoft.Xna.Framework.Color.White);
		TimeSpan timeRemaining = primaryTimer.TimeRemaining;
		string text = timeRemaining.Minutes.ToString("00") + " ";
		float x = Constants.MeasureString(Constants.Font1, text).X;
		string text2 = ":";
		float x2 = Constants.MeasureString(Constants.Font1, text2).X;
		string text3 = " " + timeRemaining.Seconds.ToString("00");
		if (SuddenDeathActive && timeRemaining.TotalSeconds > 42.0)
		{
			if (m_suddenDeathBlinkTimer <= 0f)
			{
				m_suddenDeathBlinkTimer += 125f;
				m_suddenDeathBlink = !m_suddenDeathBlink;
			}
			m_suddenDeathBlinkTimer -= ms;
		}
		else
		{
			m_suddenDeathBlink = false;
		}
		if (timeRemaining.TotalSeconds < 10.0 && m_timerTickLastSecond != timeRemaining.Seconds)
		{
			m_timerTickLastSecond = timeRemaining.Seconds;
			SoundEffectSettings soundEffectSettings = new SoundEffectSettings();
			soundEffectSettings.Pitch = m_timerTickPitch;
			m_timerTickPitch += 0.1f;
			SoundHandler.PlayGlobalSound("TimerTick", soundEffectSettings);
		}
		Constants.DrawString(m_spriteBatch, Constants.Font1, text, vector, color, 0f, new Microsoft.Xna.Framework.Vector2(x, 0f), 1f, SpriteEffects.None, 0);
		Constants.DrawString(m_spriteBatch, Constants.Font1, text2, vector, color, 0f, new Microsoft.Xna.Framework.Vector2(x2 * 0.5f, 0f), 1f, SpriteEffects.None, 0);
		Constants.DrawString(m_spriteBatch, Constants.Font1, text3, vector, color, 0f, new Microsoft.Xna.Framework.Vector2(0f, 0f), 1f, SpriteEffects.None, 0);
		if (m_suddenDeathBlink)
		{
			string text4 = LanguageHelper.GetText("menu.hostGame.suddenDeath");
			Constants.DrawString(m_spriteBatch, Constants.Font1, text4, vector + new Microsoft.Xna.Framework.Vector2(0f, 20f), color, 0f, new Microsoft.Xna.Framework.Vector2(Constants.MeasureString(Constants.Font1, text4).X * 0.5f, 0f), 1f, SpriteEffects.None, 0);
		}
	}

	public bool TestInsideWaterZone(Microsoft.Xna.Framework.Vector2 box2DPoint)
	{
		foreach (ObjectData waterZone in WaterZones)
		{
			if (!waterZone.IsDisposed && waterZone.Body.GetFixtureList().TestPoint(box2DPoint))
			{
				return true;
			}
		}
		return false;
	}

	public void UpdatePlayerReceiveItem(int playerID, NetMessage.PlayerReceiveItem.Data itemData)
	{
		Player player = GetPlayer(playerID);
		if (player != null)
		{
			player.ReceiveItem(itemData);
			return;
		}
		Queue<NetMessage.PlayerReceiveItem.Data> value = null;
		if (!m_queuedPlayerReceiveItemUpdates.TryGetValue(playerID, out value))
		{
			value = new Queue<NetMessage.PlayerReceiveItem.Data>();
			m_queuedPlayerReceiveItemUpdates.Add(playerID, value);
		}
		value.Enqueue(itemData);
	}

	public void UpdatePlayerMetaData(NetMessage.PlayerUpdateMetaData.Data data)
	{
		Player player = GetPlayer(data.PlayerID);
		if (player != null)
		{
			player.UpdatePlayerMetaData(data);
		}
		else if (m_queuedPlayerMetaDataUpdates.ContainsKey(data.PlayerID))
		{
			m_queuedPlayerMetaDataUpdates[data.PlayerID] = data;
		}
		else
		{
			m_queuedPlayerMetaDataUpdates.Add(data.PlayerID, data);
		}
	}

	public void UpdatePlayerModifierData(NetMessage.PlayerUpdateModifierData.Data data)
	{
		Player player = GetPlayer(data.PlayerID);
		if (player != null)
		{
			player.UpdatePlayerModifierData(data);
		}
		else if (m_queuedPlayerModifierDataUpdates.ContainsKey(data.PlayerID))
		{
			m_queuedPlayerModifierDataUpdates[data.PlayerID] = data;
		}
		else
		{
			m_queuedPlayerModifierDataUpdates.Add(data.PlayerID, data);
		}
	}

	public void CheckQueuedPlayerUpdates(Player player)
	{
		int objectID = player.ObjectID;
		Queue<NetMessage.PlayerReceiveItem.Data> value = null;
		if (m_queuedPlayerReceiveItemUpdates.TryGetValue(objectID, out value))
		{
			while (value.Count > 0)
			{
				player.ReceiveItem(value.Dequeue());
			}
			m_queuedPlayerReceiveItemUpdates.Remove(objectID);
		}
		if (m_queuedPlayerMetaDataUpdates.ContainsKey(objectID))
		{
			player.UpdatePlayerMetaData(m_queuedPlayerMetaDataUpdates[objectID]);
			m_queuedPlayerMetaDataUpdates.Remove(objectID);
		}
		if (m_queuedPlayerModifierDataUpdates.ContainsKey(objectID))
		{
			player.UpdatePlayerModifierData(m_queuedPlayerModifierDataUpdates[objectID]);
			m_queuedPlayerModifierDataUpdates.Remove(objectID);
		}
	}

	public void UpdateLocalPlayerVirtualInput()
	{
		if (ClearLocalPlayerVirtualInput)
		{
			SetLocalPlayerVirtualInput = 0;
			ClearLocalPlayerVirtualInput = false;
			ConsoleOutput.ShowMessage(ConsoleOutputType.GameStatus, "Clearing local player input");
			for (int i = 0; i < LocalPlayers.Length; i++)
			{
				Player player = LocalPlayers[i];
				if (player != null && !player.IsDisposed)
				{
					player.ReleaseAllVirtualKeys();
				}
			}
		}
		if (SetLocalPlayerVirtualInput <= 0)
		{
			return;
		}
		SetLocalPlayerVirtualInput--;
		if (SetLocalPlayerVirtualInput > 0)
		{
			return;
		}
		ConsoleOutput.ShowMessage(ConsoleOutputType.GameStatus, "Resuming local player input");
		for (int j = 0; j < LocalPlayers.Length; j++)
		{
			Player player2 = LocalPlayers[j];
			if (player2 != null && !player2.IsDisposed && player2.CheckInputEnabled(Player.PlayerInputType.ByRules))
			{
				player2.PressAllVirtualKeysCurrentlyPhysicallyPressed();
			}
		}
	}

	public bool HandleCommand(ProcessCommandArgs args)
	{
		Constants.SetThreadCultureInfo();
		bool flag = false;
		if (args.Parameters.Count > 0)
		{
			flag = args.Parameters[0].ToUpperInvariant() == "CLEAR" || args.Parameters[0].ToUpperInvariant() == "RESET";
		}
		if (args.ModeratorPrivileges && args.IsCommand("GAMEOVER"))
		{
			if (GameInfo.MapInfo.TypedMapType == MapType.Survival)
			{
				if (GameSFD.Handle.CurrentState == State.EditorTestRun)
				{
					GameOverData.GameOverType = GameOverType.SurvivalVictory;
					GameInfo.MapSessionData.SurvivalWaveFinished = true;
				}
				else
				{
					GameInfo.MapSessionData.SurvivalExtraLives = 0;
					GameInfo.MapSessionData.SurvivalWaveFinished = false;
					GameOverData.GameOverType = GameOverType.SurvivalLoss;
					GameOverData.WinningUserIdentifiers.Clear();
				}
			}
			SetGameOver();
			return true;
		}
		if (args.HostPrivileges && args.IsCommand("RS", "RESTART") && m_game.CurrentState == State.EditorTestRun)
		{
			if (GameInfo.MapInfo != null && GameInfo.MapInfo.TypedMapType == MapType.Campaign)
			{
				GameInfo.SetCampaignMapPart(GameInfo.GetMapPartInfoIndex());
			}
			m_restartInstant = true;
			return true;
		}
		if (args.ModeratorPrivileges && args.IsCommand("ACTIVATE"))
		{
			if (args.Parameters.Count <= 0)
			{
				return false;
			}
			if (args.Parameters[0].ToUpperInvariant() == "SLOWMOTION")
			{
				SlowmotionHandler.AddSlowmotion(new Slowmotion(100f, 5000f, 200f, 0.2f, 0));
				return true;
			}
			return false;
		}
		if (args.ModeratorPrivileges && args.IsCommand("SLOMO", "SLOWMOTION"))
		{
			if (args.Parameters.Count <= 0)
			{
				return false;
			}
			if (!(args.Parameters[0] == "1") && !(args.Parameters[0].ToUpperInvariant() == "TRUE"))
			{
				if (!(args.Parameters[0] == "0") && !(args.Parameters[0].ToUpperInvariant() == "FALSE"))
				{
					if (flag)
					{
						args.CheatInstance.SlomoTime = 1f;
						SlowmotionHandler.Reset();
						args.CheatInstance.SlomoTimeIsSet = false;
						args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, "Slowmotion reset"));
						return true;
					}
					return false;
				}
				args.CheatInstance.SlomoTime = 1f;
				SlowmotionHandler.Reset();
				args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, "Slowmotion reset"));
				return true;
			}
			args.CheatInstance.SlomoTime = 0.3f;
			SlowmotionHandler.Reset();
			SlowmotionHandler.AddSlowmotion(new Slowmotion(100f, 600000f, 1000f, 0.3f, 0));
			args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, "Slowmotion active"));
			return true;
		}
		if (args.ModeratorPrivileges && args.IsCommand("SETTIME"))
		{
			if (args.Parameters.Count <= 0)
			{
				return false;
			}
			float value = 1f;
			if (SFDXParser.TryParseFloat(args.Parameters[0], out value))
			{
				if (value < 0.1f)
				{
					value = 0.1f;
				}
				if (value > 2f)
				{
					value = 2f;
				}
				args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, "Time modifier set to x" + value.ToString("0.00")));
				SlowmotionHandler.Reset();
				if (value != 1f)
				{
					SlowmotionHandler.AddSlowmotion(new Slowmotion(100f, 86400000f, 1000f, value, 0));
				}
				args.CheatInstance.SlomoTime = value;
				return true;
			}
			if (flag)
			{
				args.CheatInstance.SlomoTime = 1f;
				SlowmotionHandler.Reset();
				args.CheatInstance.SlomoTimeIsSet = false;
				args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, "Time modifier reset"));
				return true;
			}
			return false;
		}
		if (args.ModeratorPrivileges && args.IsCommand("GIVE"))
		{
			if (args.Parameters.Count <= 1)
			{
				return false;
			}
			GameUser gameUserByStringInput = GameInfo.GetGameUserByStringInput(args.Parameters[0], args.SenderGameUser);
			Player player = null;
			if (gameUserByStringInput != null)
			{
				player = GetPlayerByUserIdentifier(gameUserByStringInput.UserIdentifier);
			}
			if (player == null)
			{
				return false;
			}
			Dictionary<string, int> dictionary = new Dictionary<string, int>();
			for (int i = 1; i < args.Parameters.Count; i++)
			{
				string text = args.Parameters[i];
				if (text.Equals("AMMO", StringComparison.InvariantCultureIgnoreCase))
				{
					bool flag2 = false;
					if (player.CurrentHandgunWeapon != null)
					{
						player.CurrentHandgunWeapon.CurrentSpareMags = player.CurrentHandgunWeapon.Properties.MaxCarriedSpareMags;
						flag2 = true;
						if (GameOwner == GameOwnerEnum.Server)
						{
							NetMessage.PlayerReceiveItem.Data data = new NetMessage.PlayerReceiveItem.Data(player.ObjectID, player.CurrentHandgunWeapon, NetMessage.PlayerReceiveItem.ReceiveSourceType.GrabWeaponAmmo);
							m_game.Server.SendMessage(MessageType.PlayerReceiveItem, data);
						}
					}
					if (player.CurrentRifleWeapon != null)
					{
						player.CurrentRifleWeapon.CurrentSpareMags = player.CurrentRifleWeapon.Properties.MaxCarriedSpareMags;
						flag2 = true;
						if (GameOwner == GameOwnerEnum.Server)
						{
							NetMessage.PlayerReceiveItem.Data data2 = new NetMessage.PlayerReceiveItem.Data(player.ObjectID, player.CurrentRifleWeapon, NetMessage.PlayerReceiveItem.ReceiveSourceType.GrabWeaponAmmo);
							m_game.Server.SendMessage(MessageType.PlayerReceiveItem, data2);
						}
					}
					if (flag2)
					{
						dictionary.AddOrIncrement("Ammo");
					}
				}
				if (text.Equals("STREETSWEEPER", StringComparison.InvariantCultureIgnoreCase) || text.Equals("STREETSWEEPERCRATE", StringComparison.InvariantCultureIgnoreCase))
				{
					SpawnObjectInformation spawnObject = new SpawnObjectInformation(CreateObjectData(text), player.PreWorld2DPosition + new Microsoft.Xna.Framework.Vector2((float)(-player.LastDirectionX) * 2f, 8f), 0f, 1, Microsoft.Xna.Framework.Vector2.Zero, 0f);
					ObjectData objectData = ObjectData.Read(CreateTile(spawnObject));
					if (objectData is ObjectStreetsweeper)
					{
						((ObjectStreetsweeper)objectData).SetOwnerPlayer(player);
						((ObjectStreetsweeper)objectData).SetOwnerTeam(player.CurrentTeam, bypassOwnerPlayerTeam: false);
					}
					else if (objectData is ObjectStreetsweeperCrate)
					{
						((ObjectStreetsweeperCrate)objectData).SetOwnerPlayer(player);
						((ObjectStreetsweeperCrate)objectData).SetOwnerTeam(player.CurrentTeam);
					}
					dictionary.AddOrIncrement("Streetsweeper");
				}
				try
				{
					SFD.Weapons.WeaponItem weapon = WeaponDatabase.GetWeapon(text);
					if (weapon != null)
					{
						goto IL_0716;
					}
					int num = int.Parse(text);
					if (num >= 0 && num < WeaponDatabase.TotalWeapons())
					{
						weapon = WeaponDatabase.GetWeapon((short)num);
						if (weapon != null)
						{
							goto IL_0716;
						}
					}
					goto end_IL_06e0;
					IL_0716:
					if (weapon.BaseProperties.WeaponCanBeEquipped)
					{
						player.GrabWeaponItem(weapon);
						dictionary.AddOrIncrement(weapon.BaseProperties.WeaponNameID);
					}
					end_IL_06e0:;
				}
				catch
				{
				}
			}
			if (dictionary.Count > 0)
			{
				string text2 = player.Name + " received ";
				for (int j = 0; j < dictionary.Count; j++)
				{
					KeyValuePair<string, int> keyValuePair = dictionary.ElementAt(j);
					text2 = ((j == dictionary.Count - 1 && dictionary.Count > 1) ? (text2 + $" and {keyValuePair.Value} {keyValuePair.Key}") : ((j <= 0) ? (text2 + $"{keyValuePair.Value} {keyValuePair.Key}") : (text2 + $", {keyValuePair.Value} {keyValuePair.Key}")));
				}
				args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, text2));
				return true;
			}
			return false;
		}
		if (args.ModeratorPrivileges && args.IsCommand("REMOVE"))
		{
			if (args.Parameters.Count <= 1)
			{
				return false;
			}
			GameUser gameUserByStringInput2 = GameInfo.GetGameUserByStringInput(args.Parameters[0], args.SenderGameUser);
			Player player2 = null;
			if (gameUserByStringInput2 != null)
			{
				player2 = GetPlayerByUserIdentifier(gameUserByStringInput2.UserIdentifier);
			}
			if (player2 == null)
			{
				return false;
			}
			try
			{
				string text3 = args.Parameters[1].ToUpperInvariant();
				SFD.Weapons.WeaponItemType weaponItemType = SFD.Weapons.WeaponItemType.NONE;
				SFD.Weapons.WeaponItem weapon2 = WeaponDatabase.GetWeapon(text3);
				if (weapon2 == null)
				{
					weaponItemType = WeaponDatabase.GetWeaponType(text3);
					if (weaponItemType == SFD.Weapons.WeaponItemType.NONE)
					{
						try
						{
							weaponItemType = (SFD.Weapons.WeaponItemType)int.Parse(text3);
						}
						catch
						{
						}
					}
				}
				else if (weapon2.BaseProperties.WeaponCanBeEquipped)
				{
					weaponItemType = weapon2.Type;
				}
				if (weaponItemType == SFD.Weapons.WeaponItemType.NONE)
				{
					weaponItemType = WeaponDatabase.GetWeaponType(text3);
				}
				if (weaponItemType == SFD.Weapons.WeaponItemType.NONE)
				{
					return false;
				}
				player2.RemoveWeaponItem(weaponItemType);
				args.Feedback.Add(new ProcessCommandMessage(args.SenderGameUser, player2.Name + " lost " + weaponItemType));
				return true;
			}
			catch
			{
				return false;
			}
		}
		return false;
	}

	public void HandleNewObjectData(NewObjectData cod)
	{
		try
		{
			switch (cod.Type)
			{
			case NewObjectType.CreateTile:
				CreateTile(cod.ReadSpawnObjectInformation(GameOwner));
				break;
			case NewObjectType.CreatePlayer:
				CreatePlayer(cod.ReadSpawnObjectInformation(GameOwner), cod.ReadProfile(), (Team)cod.ReadInt(), (Player.PlayerSpawnAnimation)cod.ReadUShort());
				break;
			case NewObjectType.Remove:
			case NewObjectType.Destroy:
			{
				int iD = cod.ReadInt();
				ObjectData objectDataByID = GetObjectDataByID(iD);
				if (objectDataByID != null)
				{
					if (cod.Type != NewObjectType.Remove && !InLoading)
					{
						objectDataByID.Destroy();
					}
					else
					{
						objectDataByID.Remove();
					}
				}
				break;
			}
			case NewObjectType.PropertyUpdate:
			{
				int num2 = cod.ReadInt();
				ObjectPropertyID propertyID = (ObjectPropertyID)cod.ReadInt();
				int num3 = cod.ReadInt();
				string value = "";
				float num4 = 0f;
				int num5 = 0;
				bool flag = false;
				switch (num3)
				{
				case 0:
					value = cod.ReadString();
					break;
				case 1:
					num4 = cod.ReadFloat();
					break;
				case 2:
					num5 = cod.ReadInt();
					break;
				case 3:
					flag = cod.ReadBoolean();
					break;
				}
				ObjectData objectData = GetObjectDataByID(num2);
				if (objectData == null && num2 == 2147483646)
				{
					objectData = ObjectWorldData;
				}
				if (objectData == null)
				{
					break;
				}
				ObjectPropertyInstance objectPropertyInstance = objectData.Properties.Get(propertyID);
				if (objectPropertyInstance != null)
				{
					switch (num3)
					{
					case 0:
						objectPropertyInstance.Value = value;
						break;
					case 1:
						objectPropertyInstance.Value = num4;
						break;
					case 2:
						objectPropertyInstance.Value = num5;
						break;
					case 3:
						objectPropertyInstance.Value = flag;
						break;
					}
				}
				break;
			}
			default:
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "TODO: GameWorld.HandleNewObject() NewObjectType." + cod.Type);
				break;
			case NewObjectType.GameWorldInformation:
			{
				NetMessage.GameWorldInformation.Data data = (NetMessage.GameWorldInformation.Data)cod.Tag;
				PropertiesWorld.SetValues(data.WorldProperties);
				int num = 0;
				while (true)
				{
					if (num >= 30)
					{
						return;
					}
					if (RenderCategories.Categories[num].TotalLayers > 0)
					{
						break;
					}
					for (int i = 0; i < data.CategoryLayersInfo[num].TotalLayers; i++)
					{
						RenderCategories[num].AddLayer(i);
						SetLayerPropertiesData(num, i);
						Layer<ObjectData> layer = RenderCategories[num].GetLayer(i);
						if (layer.Tag == null)
						{
							continue;
						}
						SFDLayerTag sFDLayerTag = (SFDLayerTag)layer.Tag;
						if (sFDLayerTag != null)
						{
							if (sFDLayerTag.Object != null)
							{
								sFDLayerTag.Properties.SetValues(data.CategoryLayersInfo[num].LayerProperties[i], networkSilent: true);
							}
						}
						else
						{
							ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "GameWorldInformation layerTag is null, this should never happen");
						}
					}
					num++;
				}
				throw new NotImplementedException("GameWorldInformation must be run first in the loading sequence.");
			}
			case NewObjectType.SetAllowedCameraModes:
			{
				int allowedCameraModes = cod.ReadInt();
				SetAllowedCameraModes((CameraMode)allowedCameraModes);
				break;
			}
			case NewObjectType.SetCurrentCameraMode:
			{
				int cameraModeForPlayers = cod.ReadInt();
				SetCameraModeForPlayers((CameraMode)cameraModeForPlayers);
				break;
			}
			case NewObjectType.GroupSpawnSync:
				GroupSyncFinilize(cod.ReadUShort());
				break;
			}
		}
		catch (Exception ex)
		{
			throw new Exception("Error: Could not handle new object\r\n" + ex.ToString());
		}
	}

	public Body CreateTile(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPosition, float angle)
	{
		SpawnObjectInformation spawnObject = new SpawnObjectInformation(IDCounter.NextObjectData(mapObjectID), worldPosition, angle);
		return CreateTile(spawnObject);
	}

	public Body CreateTileTopLayer(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPosition, float angle, short faceDirection, Microsoft.Xna.Framework.Vector2 linearVelocity, float angularVelocity)
	{
		SpawnObjectInformation spawnObjectInformation = new SpawnObjectInformation(IDCounter.NextObjectData(mapObjectID), worldPosition, angle, faceDirection, linearVelocity, angularVelocity);
		if (spawnObjectInformation.ObjectData != null && spawnObjectInformation.ObjectData.Tile != null && RenderCategories != null && RenderCategories.Length >= spawnObjectInformation.ObjectData.Tile.DrawCategory)
		{
			spawnObjectInformation.Layer = Math.Max(RenderCategories[spawnObjectInformation.ObjectData.Tile.DrawCategory].TotalLayers - 1, 0);
		}
		return CreateTile(spawnObjectInformation);
	}

	public Body CreateTile(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPosition, float angle, short faceDirection, Microsoft.Xna.Framework.Vector2 linearVelocity, float angularVelocity)
	{
		SpawnObjectInformation spawnObject = new SpawnObjectInformation(IDCounter.NextObjectData(mapObjectID), worldPosition, angle, faceDirection, linearVelocity, angularVelocity);
		return CreateTile(spawnObject);
	}

	public Body CreateLocalTile(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPosition, float angle)
	{
		SpawnObjectInformation spawnObject = new SpawnObjectInformation(IDCounter.NextLocalObjectData(mapObjectID), worldPosition, angle);
		return CreateTile(spawnObject);
	}

	public Body CreateLocalTile(string mapObjectID, Microsoft.Xna.Framework.Vector2 worldPosition, float angle, short faceDirection, Microsoft.Xna.Framework.Vector2 linearVelocity, float angularVelocity)
	{
		SpawnObjectInformation spawnObject = new SpawnObjectInformation(IDCounter.NextLocalObjectData(mapObjectID), worldPosition, angle, faceDirection, linearVelocity, angularVelocity);
		return CreateTile(spawnObject);
	}

	public void InitKickedObjects()
	{
		m_kickedObjects = new List<ObjectData>();
	}

	public void DisposeKickedObjects()
	{
		m_kickedObjects.Clear();
		m_kickedObjects = null;
	}

	public void TrackKickedObject(Player player, ObjectData od)
	{
		od.IgnoreKickVelocityForPlayerID = player.ObjectID;
		od.IgnoreKickVelocityForPlayerElapsedTotalTime = ElapsedTotalGameTime;
		if (!m_kickedObjects.Contains(od))
		{
			m_kickedObjects.Add(od);
		}
	}

	public void UpdateKickedObjects()
	{
		if (m_kickedObjects.Count <= 0)
		{
			return;
		}
		for (int num = m_kickedObjects.Count - 1; num >= 0; num--)
		{
			ObjectData objectData = m_kickedObjects[num];
			if (objectData.IsDisposed || objectData.IgnoreKickVelocityForPlayerElapsedTotalTime + 500f < ElapsedTotalGameTime)
			{
				objectData.IgnoreKickVelocityForPlayerID = 0;
				m_kickedObjects.RemoveAt(num);
			}
		}
	}

	public bool MeleePendingPlayerHit(Player player)
	{
		return m_meleePlayersList.Contains(player);
	}

	public void MeleeAddPlayerHit(Player player)
	{
		if (!m_meleePlayersList.Contains(player))
		{
			m_meleePlayersList.Add(player);
		}
	}

	public bool KickPendingPlayerHit(Player player)
	{
		return m_kickPlayersList.Contains(player);
	}

	public void KickAddPlayerHit(Player player)
	{
		if (!m_kickPlayersList.Contains(player))
		{
			m_kickPlayersList.Add(player);
		}
	}

	public void MeleeKickUpdate()
	{
		if (m_kickPlayersList.Count > 0)
		{
			if (m_kickPlayersList.Count > 1)
			{
				List<Player> list = new List<Player>();
				list.AddRange(m_kickPlayersList);
				m_kickPlayersList.Clear();
				for (int num = list.Count - 1; num >= 0; num--)
				{
					int index = Constants.RANDOM.Next(0, list.Count);
					m_kickPlayersList.Add(list[index]);
					list.RemoveAt(index);
				}
			}
			for (int num2 = m_kickPlayersList.Count - 1; num2 >= 0; num2--)
			{
				Player player = m_kickPlayersList[num2];
				if (!player.IsDisposed && !player.IsRemoved && (!player.Disabled || player.FullLandingOnGround) && GameOwner != GameOwnerEnum.Client)
				{
					player.HitObjectsInKick();
				}
			}
			m_kickPlayersList.Clear();
		}
		if (m_meleePlayersList.Count <= 0)
		{
			return;
		}
		if (m_meleePlayersList.Count > 1)
		{
			List<Player> list2 = new List<Player>();
			list2.AddRange(m_meleePlayersList);
			m_meleePlayersList.Clear();
			for (int num3 = list2.Count - 1; num3 >= 0; num3--)
			{
				int index2 = Constants.RANDOM.Next(0, list2.Count);
				m_meleePlayersList.Add(list2[index2]);
				list2.RemoveAt(index2);
			}
		}
		for (int num4 = m_meleePlayersList.Count - 1; num4 >= 0; num4--)
		{
			Player player2 = m_meleePlayersList[num4];
			if (!player2.IsRemoved && (!player2.Disabled || player2.FullLandingOnGround) && !player2.HitObjectsInMelee(GameOwner == GameOwnerEnum.Client))
			{
				if (player2.CurrentAction == PlayerAction.MeleeAttack3)
				{
					player2.TimeSequence.TimeMeleeMovement = 135f;
				}
				else
				{
					player2.TimeSequence.TimeMeleeMovement = 1f;
				}
			}
		}
		m_meleePlayersList.Clear();
	}

	public List<ObjectData> GetMissileUpdateObjects()
	{
		return MissileUpdateObjects;
	}

	public void AddMissileObject(ObjectData objectData, ObjectMissileStatus status, Player ownerPlayer = null)
	{
		if (!objectData.IsDisposed && !objectData.TerminationInitiated)
		{
			if (!MissileUpdateObjects.Contains(objectData))
			{
				MissileUpdateObjects.Add(objectData);
			}
			if (objectData.MissileData == null)
			{
				objectData.MissileData = new ObjectMissileData();
			}
			objectData.MissileData.PrevPosition = objectData.Body.GetPosition();
			objectData.MissileData.PrevLinearVelocity = objectData.Body.GetLinearVelocity();
			objectData.MissileData.PrevAngle = objectData.Body.GetAngle();
			objectData.MissileData.LastMoveSequence = objectData.BodyData.MoveSequence;
			if (objectData.MissileData.Status < status)
			{
				objectData.MissileData.Status = status;
			}
			if (ownerPlayer != null)
			{
				objectData.MissileData.PlayerSource = ownerPlayer;
				objectData.MissileData.IgnorePlayer(ownerPlayer);
			}
		}
	}

	public void RemoveMissileObject(ObjectData objectData)
	{
		objectData.MissileData = null;
		MissileUpdateObjects.Remove(objectData);
	}

	public void CalculateMissileRCIPoints(Box2D.XNA.RayCastInput[] rci, ObjectData od)
	{
		rci[0].maxFraction = 1f;
		rci[0].p1 = od.MissileData.PrevPosition;
		rci[0].p2 = od.Body.GetPosition();
		rci[1].maxFraction = 1f;
		rci[1].p1 = rci[0].p1;
		rci[1].p2 = rci[0].p2;
		rci[2].maxFraction = 1f;
		rci[2].p1 = rci[0].p1;
		rci[2].p2 = rci[0].p2;
		if (od.MissileData.CachedEndPointVertices == null)
		{
			Fixture fixture = od.Body.GetFixtureList();
			byte b = 0;
			Microsoft.Xna.Framework.Vector2 zero = Microsoft.Xna.Framework.Vector2.Zero;
			Microsoft.Xna.Framework.Vector2 zero2 = Microsoft.Xna.Framework.Vector2.Zero;
			while (fixture != null && b <= 3)
			{
				Shape shape = fixture.GetShape();
				if (shape is PolygonShape)
				{
					Microsoft.Xna.Framework.Vector2[] verticesArray = ((PolygonShape)shape).GetVerticesArray();
					for (int i = 0; i < verticesArray.Length; i++)
					{
						zero.X = Math.Min(zero.X, verticesArray[i].X);
						zero.Y = Math.Min(zero.Y, verticesArray[i].Y);
						zero2.X = Math.Max(zero2.X, verticesArray[i].X);
						zero2.Y = Math.Max(zero2.Y, verticesArray[i].Y);
					}
				}
				fixture = fixture.GetNext();
				b++;
			}
			bool num = Math.Abs(zero2.X - zero.X) > Math.Abs(zero2.Y - zero.Y);
			Microsoft.Xna.Framework.Vector2[] array = new Microsoft.Xna.Framework.Vector2[2];
			if (num)
			{
				array[0] = new Microsoft.Xna.Framework.Vector2(Math.Min(zero.X + 0.08f, 0f), 0f);
				array[1] = new Microsoft.Xna.Framework.Vector2(Math.Max(zero2.X - 0.08f, 0f), 0f);
			}
			else
			{
				array[0] = new Microsoft.Xna.Framework.Vector2(0f, Math.Min(zero.Y + 0.08f, 0f));
				array[1] = new Microsoft.Xna.Framework.Vector2(0f, Math.Max(zero2.Y - 0.08f, 0f));
			}
			od.MissileData.CachedEndPointVertices = array;
		}
		Microsoft.Xna.Framework.Vector2 position = od.MissileData.CachedEndPointVertices[0];
		rci[1].maxFraction = 1f;
		rci[1].p2 = od.Body.GetWorldPoint(position);
		SFDMath.RotatePosition(ref position, od.MissileData.PrevAngle, out position);
		rci[1].p1 = od.MissileData.PrevPosition + position;
		position = od.MissileData.CachedEndPointVertices[1];
		rci[2].maxFraction = 1f;
		rci[2].p2 = od.Body.GetWorldPoint(position);
		SFDMath.RotatePosition(ref position, od.MissileData.PrevAngle, out position);
		rci[2].p1 = od.MissileData.PrevPosition + position;
	}

	public void UpdateMissileObjectAfterBox2DStep(float timestep)
	{
		if (MissileUpdateObjects.Count <= 0)
		{
			return;
		}
		if (m_missileRCICache == null)
		{
			m_missileRCICache = new Box2D.XNA.RayCastInput[3];
		}
		for (int num = MissileUpdateObjects.Count - 1; num >= 0; num--)
		{
			ObjectData objectData = MissileUpdateObjects[num];
			if (!objectData.IsDisposed && objectData.MissileData != null)
			{
				if (objectData.TerminationInitiated)
				{
					if (objectData.MissileData.TerminationHitTestDone)
					{
						continue;
					}
					objectData.MissileData.TerminationHitTestDone = true;
				}
				objectData.MissileData.HitCooldown -= timestep;
				if (objectData.MissileData.LastMoveSequence != objectData.BodyData.MoveSequence)
				{
					objectData.MissileData.LastMoveSequence = objectData.BodyData.MoveSequence;
					objectData.MissileData.PrevPosition = objectData.Body.GetPosition();
					objectData.MissileData.PrevLinearVelocity = objectData.Body.GetLinearVelocity();
					objectData.MissileData.PrevAngle = objectData.Body.GetAngle();
					continue;
				}
				Player player = null;
				int num2 = 0;
				float value = 0f;
				float num3 = 999f;
				Microsoft.Xna.Framework.Vector2 hitNormal = Microsoft.Xna.Framework.Vector2.Zero;
				CalculateMissileRCIPoints(m_missileRCICache, objectData);
				if (objectData.MissileData.PlayerSource != null)
				{
					if (!objectData.MissileData.PlayerSource.IsDisposed && objectData.MissileData.PlayerSource.WorldBody != null)
					{
						Microsoft.Xna.Framework.Vector2 vector = objectData.GetBox2DCenterPosition() - objectData.MissileData.PlayerSource.PreBox2DPosition;
						if (Math.Abs(vector.X) > 0.96f || Math.Abs(vector.Y) > 1.4399999f)
						{
							objectData.MissileData.PlayerSource = null;
						}
					}
					else
					{
						objectData.MissileData.PlayerSource = null;
					}
				}
				RayCastOutput output;
				for (int i = 0; i < Players.Count; i++)
				{
					Player player2 = Players[i];
					if (player2.IsDisposed || player2.WorldBody == null || objectData.MissileData.PlayerSource == player2)
					{
						continue;
					}
					if (player2.CheckMissileDeflection(objectData, m_missileRCICache))
					{
						objectData.MissileData.HitCooldown = 0f;
					}
					else
					{
						if (!(objectData.MissileData.HitCooldown <= 0f))
						{
							continue;
						}
						int num4 = -1;
						player2.GetAABBMissileHitbox(out var aabb);
						bool flag = false;
						if (player2.IsDead && player2.LayingOnGround)
						{
							num4 = 0;
							flag = aabb.RayCast(out output, ref m_missileRCICache[0], startInsideCollision: true);
						}
						else
						{
							flag = aabb.RayCast(out output, ref m_missileRCICache[++num4], startInsideCollision: true) || aabb.RayCast(out output, ref m_missileRCICache[++num4], startInsideCollision: true) || aabb.RayCast(out output, ref m_missileRCICache[++num4], startInsideCollision: true);
						}
						if (flag)
						{
							if (objectData.MissileData.HitObjectIDs.ContainsKey(player2.ObjectID))
							{
								objectData.MissileData.HitObjectIDs[player2.ObjectID] = ElapsedTotalGameTime;
								continue;
							}
							Microsoft.Xna.Framework.Vector2 vector2 = ((objectData.Body.GetLinearVelocity().CalcSafeLength() > objectData.MissileData.PrevLinearVelocity.CalcSafeLength()) ? objectData.Body.GetLinearVelocity() : objectData.MissileData.PrevLinearVelocity);
							Microsoft.Xna.Framework.Vector2 x = vector2 - player2.WorldBody.GetLinearVelocity();
							if (objectData.MissileData.Status == ObjectMissileStatus.Thrown && objectData.MissileData.ContactTime <= 0f)
							{
								if (x.CalcSafeLength() < 1f && vector2.CalcSafeLength() < 1f)
								{
									continue;
								}
							}
							else
							{
								if (x.Y > 0f && objectData.MissileData.Status != ObjectMissileStatus.Thrown)
								{
									x.Y = SFDMath.DampenTowardsZero(x.Y, 4f);
								}
								if (x.Y > 0f && objectData.MissileData.ContactTime > 0f)
								{
									x.Y = SFDMath.DampenTowardsZero(x.Y, 4f);
								}
								if (x.CalcSafeLength() < 7f && !(vector2.CalcSafeLength() >= 7f))
								{
									continue;
								}
							}
							if (!(output.fraction < num3))
							{
								continue;
							}
							MissileBeforeHitEventArgs e = new MissileBeforeHitEventArgs();
							objectData.BeforeMissileHitObject(player2.ObjectData, e);
							if (!e.Cancel)
							{
								if (player2.TestMissileHit(objectData))
								{
									num3 = output.fraction;
									hitNormal = output.normal;
									player = player2;
									num2 = num4;
								}
								else
								{
									objectData.MissileData.IgnorePlayer(player2);
								}
							}
						}
						else if (objectData.MissileData.HitObjectIDs.TryGetValue(player2.ObjectID, out value) && ElapsedTotalGameTime - value >= 300f)
						{
							objectData.MissileData.HitObjectIDs.Remove(player2.ObjectID);
						}
					}
				}
				if (player != null)
				{
					MissileHitEventArgs e2 = new MissileHitEventArgs();
					e2.HitBox2DPosition = m_missileRCICache[num2].GetHitPosition(num3);
					e2.HitNormal = hitNormal;
					objectData.MissileHitPlayer(player, e2);
					if (objectData.MissileData.Status == ObjectMissileStatus.Thrown)
					{
						objectData.MissileData.Status = ObjectMissileStatus.Dropped;
					}
					if (objectData.GameOwner == GameOwnerEnum.Server)
					{
						objectData.GameWorld.AddForcedPositionUpdate(objectData.BodyData);
					}
					objectData.MissileData.IgnorePlayer(player);
				}
				else
				{
					foreach (ObjectStreetsweeper streetsweeper in Streetsweepers)
					{
						if (streetsweeper.IsDisposed)
						{
							continue;
						}
						int num5 = -1;
						Fixture bodyFixture = streetsweeper.GetBodyFixture();
						Box2D.XNA.RayCastInput[] missileRCICache = m_missileRCICache;
						num5 = 0;
						if (bodyFixture.RayCast(out output, ref missileRCICache[0], startInsideCollision: true) || bodyFixture.RayCast(out output, ref m_missileRCICache[++num5], startInsideCollision: true) || bodyFixture.RayCast(out output, ref m_missileRCICache[++num5], startInsideCollision: true))
						{
							MissileBeforeHitEventArgs e3 = new MissileBeforeHitEventArgs();
							objectData.BeforeMissileHitObject(streetsweeper, e3);
							if (e3.Cancel)
							{
								continue;
							}
							if (objectData.MissileData.HitObjectIDs.ContainsKey(streetsweeper.ObjectID))
							{
								objectData.MissileData.HitObjectIDs[streetsweeper.ObjectID] = ElapsedTotalGameTime;
								continue;
							}
							objectData.MissileData.IgnoreObject(streetsweeper);
							Microsoft.Xna.Framework.Vector2 hitPosition = m_missileRCICache[num5].GetHitPosition(output.fraction);
							Material.HandleMeleeVsObject(objectData.Tile.GetTileFixtureMaterial(0), streetsweeper, bodyFixture, PlayerHitAction.Punch, Converter.Box2DToWorld(hitPosition), this);
							if (GameOwner != GameOwnerEnum.Client)
							{
								float impactDamage = ObjectDataMethods.DefaultGetMissileHitObjectDamage(objectData, streetsweeper);
								streetsweeper.ImpactHit(objectData, new ImpactHitEventArgs(bodyFixture, objectData.Body.GetFixtureList(), hitPosition, output.normal, impactDamage, ImpactHitCause.ServerPlayerEvent));
								SFD.Weapons.WeaponItem weaponItem = null;
								if (objectData is ObjectWeaponItem)
								{
									weaponItem = ((ObjectWeaponItem)objectData).GetWeaponItem();
								}
								objectData.MissileData.SetHitCooldown();
								if (weaponItem != null && weaponItem.Type == SFD.Weapons.WeaponItemType.Melee)
								{
									weaponItem.MWeaponData.Durability.CurrentValue -= weaponItem.MWeaponData.Properties.ThrownDurabilityLossOnHitPlayers;
									objectData.Health.Fullness = weaponItem.MWeaponData.Durability.Fullness;
									if (objectData.Health.IsEmpty)
									{
										objectData.Destroy();
									}
								}
							}
							streetsweeper.Body.ApplyLinearImpulse(objectData.Body.GetLinearVelocity() * objectData.Body.GetMass() * 20f * timestep, Microsoft.Xna.Framework.Vector2.Zero);
							objectData.Body.SetTransform(hitPosition, objectData.Body.GetAngle());
							objectData.Body.SetLinearVelocity(objectData.Body.GetLinearVelocity() * -0.2f + Microsoft.Xna.Framework.Vector2.UnitY * 1f);
							objectData.Body.SetAngularVelocity((0f - objectData.Body.GetAngularVelocity()) * -0.5f);
							if (GameOwner == GameOwnerEnum.Server)
							{
								objectData.GameWorld.AddForcedPositionUpdate(objectData.BodyData);
							}
							if (objectData.MissileData.Status == ObjectMissileStatus.Thrown)
							{
								objectData.MissileData.Status = ObjectMissileStatus.Dropped;
							}
						}
						else if (objectData.MissileData.HitObjectIDs.TryGetValue(streetsweeper.ObjectID, out value) && ElapsedTotalGameTime - value >= 300f)
						{
							objectData.MissileData.HitObjectIDs.Remove(streetsweeper.ObjectID);
						}
					}
				}
				objectData.MissileData.PrevPosition = objectData.Body.GetPosition();
				objectData.MissileData.PrevLinearVelocity = objectData.Body.GetLinearVelocity();
				objectData.MissileData.PrevAngle = objectData.Body.GetAngle();
				if (!objectData.Body.HasTouchingContact(delegate(ContactEdge contactEdge)
				{
					if (contactEdge.Contact.GetFixtureA() != null && contactEdge.Contact.GetFixtureB() != null)
					{
						contactEdge.Contact.GetFixtureA().GetFilterData(out var filter);
						contactEdge.Contact.GetFixtureB().GetFilterData(out var filter2);
						if (!filter.isCloud && !filter2.isCloud)
						{
							return !contactEdge.Contact.LastContactEnabledFlag;
						}
						return contactEdge.Contact.LastContactEnabledFlag;
					}
					return false;
				}) && objectData.Body.GetLinearVelocity().CalcSafeLength() >= 0.1f)
				{
					if (objectData.MissileData.ContactTime > 0f)
					{
						objectData.MissileData.ContactTime -= timestep;
						if (objectData.MissileData.ContactTime < 0f)
						{
							objectData.MissileData.ContactTime = 0f;
						}
					}
				}
				else
				{
					objectData.MissileData.ContactTime += timestep;
					if (objectData.MissileData.ContactTime < 0.05f)
					{
						objectData.MissileData.ContactTime = 0.05f;
					}
					if (objectData.MissileData.Status == ObjectMissileStatus.Thrown && (objectData.TotalExistTime > 100f || objectData.Body.GetLinearVelocity().CalcSafeLength() < 5f))
					{
						objectData.MissileData.Status = ObjectMissileStatus.Dropped;
					}
				}
				if (objectData.MissileData.ContactTime > 0.15f)
				{
					RemoveMissileObject(objectData);
				}
			}
			else
			{
				RemoveMissileObject(objectData);
			}
		}
	}

	public void InitMultiPacket()
	{
		if (GameOwner == GameOwnerEnum.Server)
		{
			m_multiPacket = (m_game.Server?.NetServer)?.CreateMessage();
		}
		else if (GameOwner == GameOwnerEnum.Client)
		{
			m_multiPacket = (m_game.Client?.NetClient)?.CreateMessage();
		}
	}

	public bool IsMultiPacketEmpty()
	{
		return m_multiPacket.LengthBits == 0;
	}

	public bool IsMultiPacketFull()
	{
		return m_multiPacket.LengthBytes > 160;
	}

	public NetOutgoingMessage GetMultiPacket()
	{
		if (m_multiPacket.LengthBytes > 160)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "MultiPacket is over 160 bytes, sending!");
			SendMultiPacket();
		}
		if (m_multiPacket.LengthBits == 0)
		{
			NetMessage.WriteDataType(MessageType.MultiPacket, m_multiPacket);
		}
		return m_multiPacket;
	}

	public void SendMultiPacket()
	{
		if (m_multiPacket.LengthBits > 0)
		{
			NetMessage.WriteDataType(MessageType.None, m_multiPacket);
			if (GameOwner == GameOwnerEnum.Server)
			{
				m_game.Server.NetServer.SendToAll(m_multiPacket, NetDeliveryMethod.Unreliable);
			}
			else if (GameOwner == GameOwnerEnum.Client)
			{
				m_game.Client.NetClient.SendMessage(m_multiPacket, NetDeliveryMethod.Unreliable);
			}
			DisposeMultiPacket();
			InitMultiPacket();
		}
	}

	public void DisposeMultiPacket()
	{
		if (m_multiPacket != null)
		{
			NetPeerPool.Instance.Recycle(m_multiPacket);
			m_multiPacket = null;
		}
	}

	public void AddCleanObject(ObjectData objectData)
	{
		for (int i = 0; i < ObjectCleanUpdates.Count; i++)
		{
			if (ObjectCleanUpdates[i] == objectData)
			{
				return;
			}
		}
		ObjectCleanUpdates.Add(objectData);
	}

	public void HandleObjectCleanCycle()
	{
		if (ObjectCleanUpdates.Count <= 0)
		{
			return;
		}
		int count = ObjectCleanUpdates.Count;
		if (GameOwner != GameOwnerEnum.Client)
		{
			for (int i = 0; i < count; i++)
			{
				ObjectData objectData = ObjectCleanUpdates[i];
				if (objectData.IsPlayer && objectData.InternalData != null)
				{
					((Player)objectData.InternalData).ProcessRemovedAddedWeaponsCallback();
				}
			}
			RunScriptOnObjectTerminatedCallbacks(ObjectCleanUpdates);
		}
		for (int num = count - 1; num >= 0; num--)
		{
			ObjectData objectData2 = ObjectCleanUpdates[num];
			if (objectData2.TerminationInitiated)
			{
				objectData2.SetGroupID(0);
				if (GameOwner == GameOwnerEnum.Server && objectData2.ObjectID > 0)
				{
					NewObjectData newObjectData = new NewObjectData(objectData2.DestructionInitiated ? NewObjectType.Destroy : NewObjectType.Remove);
					newObjectData.Write(objectData2.ObjectID);
					NewObjectsCollection.AddNewObject(newObjectData);
				}
				objectData2.DisableUpdateObject();
				if (objectData2.DestructionInitiated && objectData2.DoDestroyObject)
				{
					objectData2.OnDestroyObject();
					objectData2.OnDestroyGenericCheck();
				}
				objectData2.OnRemoveObject();
				if (Weather != null)
				{
					WeatherRemoveObject(objectData2);
				}
				if (objectData2.Owners != null)
				{
					foreach (Fixture owner in objectData2.Owners)
					{
						owner.GetAABB(out var aabb);
						owner.GetFilterData(out var filter);
						b2_world_active.WakeBodies(ref aabb, ref filter);
					}
				}
				DisposedObjectIDs.Add(objectData2.ObjectID);
				objectData2.Dispose();
			}
		}
		ObjectCleanUpdates.RemoveRange(0, count);
		foreach (Player player in Players)
		{
			if (player.StandingOnBody != null && player.StandingOnBody.GetFixtureList() == null)
			{
				player.StandingOnBody = null;
			}
		}
	}

	public void AddUpdateColorObject(ObjectData objectData)
	{
		if (!InLoading && GameOwner != GameOwnerEnum.Server && objectData.HasColors)
		{
			for (int i = 0; i < ObjectColorUpdates.Count; i++)
			{
				if (ObjectColorUpdates[i] == objectData)
				{
					return;
				}
			}
			ObjectColorUpdates.Add(objectData);
		}
		else
		{
			objectData.UpdateColorsInner();
		}
	}

	public void HandleColorUpdateObjects()
	{
		if (ObjectColorUpdates.Count <= 0)
		{
			return;
		}
		lock (m_disposeLockObject)
		{
			if (GameOwner == GameOwnerEnum.Client)
			{
				if (GameSFD.Handle.Client != null)
				{
					lock (Client.ClientUpdateLockObject)
					{
						HandleColorUpdateObjectsInner();
						return;
					}
				}
			}
			else if (GameOwner == GameOwnerEnum.Server)
			{
				if (GameSFD.Handle.Server != null)
				{
					lock (Server.ServerUpdateLockObject)
					{
						HandleColorUpdateObjectsInner();
						return;
					}
				}
			}
			else
			{
				HandleColorUpdateObjectsInner();
			}
		}
	}

	public void HandleColorUpdateObjectsInner()
	{
		if (IsDisposed || ObjectColorUpdates.Count == 0)
		{
			return;
		}
		lock (Program.DRIVER_LOCKLESS ? ObjectColorUpdates : GameSFD.SpriteBatchResourceObject)
		{
			for (int i = 0; i < ObjectColorUpdates.Count; i++)
			{
				ObjectData objectData = ObjectColorUpdates[i];
				if (!objectData.IsDisposed && !objectData.TerminationInitiated)
				{
					objectData.UpdateColorsInner();
				}
			}
			ObjectColorUpdates.Clear();
		}
	}

	public static void UpdatePendingColorUpdates(GameWorld gameWorld)
	{
		gameWorld?.HandleColorUpdateObjects();
	}

	public void AddUpdateObject(ObjectData objectData)
	{
		if (!ObjectUpdateCycleUpdatesToRemove.Remove(objectData) && !ObjectUpdateCycleUpdatesToAdd.Contains(objectData))
		{
			ObjectUpdateCycleUpdatesToAdd.Add(objectData);
		}
	}

	public void RemoveUpdateObject(ObjectData objectData)
	{
		if (!ObjectUpdateCycleUpdatesToAdd.Remove(objectData) && !ObjectUpdateCycleUpdatesToRemove.Contains(objectData))
		{
			ObjectUpdateCycleUpdatesToRemove.Add(objectData);
		}
	}

	public void HandleObjectUpdateCycle(float ms)
	{
		if (ObjectUpdateCycleUpdatesToRemove.Count > 0)
		{
			for (int i = 0; i < ObjectUpdateCycleUpdatesToRemove.Count; i++)
			{
				ObjectUpdateCycleUpdates.Remove(ObjectUpdateCycleUpdatesToRemove[i]);
			}
			ObjectUpdateCycleUpdatesToRemove.Clear();
		}
		if (ObjectUpdateCycleUpdatesToAdd.Count > 0)
		{
			for (int j = 0; j < ObjectUpdateCycleUpdatesToAdd.Count; j++)
			{
				ObjectUpdateCycleUpdates.Add(ObjectUpdateCycleUpdatesToAdd[j]);
			}
			ObjectUpdateCycleUpdatesToAdd.Clear();
		}
		int count = ObjectUpdateCycleUpdates.Count;
		for (int k = 0; k < count; k++)
		{
			ObjectData objectData = ObjectUpdateCycleUpdates[k];
			if (!objectData.IsDisposed)
			{
				objectData.UpdateObject(ms);
			}
		}
	}

	public void UpdateObjectBeforeBox2DStep(float timestep)
	{
		int count = ObjectUpdateCycleUpdates.Count;
		for (int i = 0; i < count; i++)
		{
			ObjectData objectData = ObjectUpdateCycleUpdates[i];
			if (!objectData.IsDisposed)
			{
				objectData.UpdateObjectBeforeBox2DStep(timestep);
			}
		}
	}

	public void HandleCheckActivateOnStartupObjects()
	{
		int count = CheckActivateOnStartupObjects.Count;
		if (count <= 0)
		{
			return;
		}
		for (int i = 0; i < count; i++)
		{
			ObjectData objectData = CheckActivateOnStartupObjects[i];
			if (!objectData.TerminationInitiated && !objectData.IsDisposed && objectData is ObjectTriggerBase)
			{
				ObjectTriggerBase objectTriggerBase = (ObjectTriggerBase)objectData;
				if (objectTriggerBase.ActivateOnStartup)
				{
					objectTriggerBase.TriggerNode(null);
				}
			}
		}
		CheckActivateOnStartupObjects.RemoveRange(0, count);
	}

	public void UpdatePortals()
	{
		foreach (Player player in Players)
		{
			if (player.IsRemoved || player.IsGrabbedByPlayer || player.IsCaughtByPlayer || (player.GameOwner == GameOwnerEnum.Server && !player.IsServerSideControls && !player.ServerForceControlsMovement && !player.Diving) || (player.GameOwner == GameOwnerEnum.Client && ((player.RocketRideProjectileWorldID == 0 && player.ServerForceControlsMovement) || !player.HasLocalControl)))
			{
				continue;
			}
			foreach (GameWorldPortal portal in Portals)
			{
				if (!((portal.Source.GetBody() != null) & (portal.Destination.GetBody() != null) & (portal.Source.GetShape() != null) & (portal.Destination.GetShape() != null)))
				{
					continue;
				}
				Microsoft.Xna.Framework.Vector2 vector = player.WorldBody.GetLinearVelocity() - portal.Source.GetBody().GetLinearVelocity();
				bool flag = vector.X > 0.4f || (player.RocketRideProjectile == null && player.Movement == PlayerMovement.Right) || (player.RocketRideProjectile != null && player.RocketRideProjectile.Direction.X > 0.1f);
				bool flag2 = vector.X < -0.4f || (player.RocketRideProjectile == null && player.Movement == PlayerMovement.Left) || (player.RocketRideProjectile != null && player.RocketRideProjectile.Direction.X < -0.1f);
				if ((!(portal.EnterDirection == PlayerMovement.Right && flag) && !(portal.EnterDirection == PlayerMovement.Left && flag2)) || !portal.Source.TestPoint(player.WorldBody.GetPosition() + new Microsoft.Xna.Framework.Vector2(0f, 0.1f)))
				{
					continue;
				}
				Microsoft.Xna.Framework.Vector2 vector2 = player.WorldBody.GetPosition() - portal.Source.GetBody().GetPosition();
				if (portal.EnterDirection != portal.ExitDirection)
				{
					player.WorldBody.SetTransform(portal.Destination.GetBody().GetPosition() + vector2, 0f);
					player.FlipMovement(portal.ExitDirection, portalFlip: true, portal.Destination.GetBody().GetLinearVelocity() - portal.Source.GetBody().GetLinearVelocity());
					if (player.HoldingPlayerInGrab != null)
					{
						player.HoldingPlayerInGrab.WorldBody.SetTransform(portal.Destination.GetBody().GetPosition() + vector2, 0f);
						player.HoldingPlayerInGrab.ForceServerPositionState();
					}
				}
				else
				{
					vector2.X *= -1f;
					player.WorldBody.SetTransform(portal.Destination.GetBody().GetPosition() + vector2, 0f);
					player.FlipMovement(portal.ExitDirection, portalFlip: false, portal.Destination.GetBody().GetLinearVelocity() - portal.Source.GetBody().GetLinearVelocity());
					if (player.HoldingPlayerInGrab != null)
					{
						player.HoldingPlayerInGrab.WorldBody.SetTransform(portal.Destination.GetBody().GetPosition() + vector2, 0f);
						player.HoldingPlayerInGrab.ForceServerPositionState();
					}
				}
				player.PreBox2DPosition = player.WorldBody.GetPosition();
				player.ImportantUpdate = true;
				if (player.ServerForceControlsMovement)
				{
					player.ForceServerPositionState();
				}
				if (player.RocketRideProjectile != null)
				{
					player.RocketRideProjectile.Position = Converter.ConvertBox2DToWorld(player.WorldBody.GetPosition());
					if (portal.EnterDirection != portal.ExitDirection)
					{
						player.RocketRideProjectile.Velocity = player.RocketRideProjectile.Velocity * new Microsoft.Xna.Framework.Vector2(-1f, 1f);
					}
					player.RocketRideProjectile.ImportantUpdate = true;
				}
				player.MarkTeleported();
				break;
			}
		}
		if (GameOwner == GameOwnerEnum.Client)
		{
			return;
		}
		foreach (ObjectData item in PortalsObjectsToKeepTrackOf)
		{
			foreach (GameWorldPortal portal2 in Portals)
			{
				if (((portal2.Source.GetBody() != null) & (portal2.Destination.GetBody() != null) & (portal2.Source.GetShape() != null) & (portal2.Destination.GetShape() != null)) && ((portal2.EnterDirection == PlayerMovement.Right && item.Body.GetLinearVelocity().X > 0.1f) || (portal2.EnterDirection == PlayerMovement.Left && item.Body.GetLinearVelocity().X < -0.1f)) && portal2.Source.TestPoint(item.Body.GetPosition()))
				{
					Microsoft.Xna.Framework.Vector2 vector3 = item.Body.GetPosition() - portal2.Source.GetBody().GetPosition();
					if (vector3.Y < 0f)
					{
						vector3.Y += 0.08f;
					}
					if (portal2.EnterDirection != portal2.ExitDirection)
					{
						item.Body.SetTransform(portal2.Destination.GetBody().GetPosition() + vector3, 0f - item.Body.GetAngle());
						item.Body.SetLinearVelocity(item.Body.GetLinearVelocity() * new Microsoft.Xna.Framework.Vector2(-1f, 1f));
						item.Body.SetAngularVelocity(0f - item.Body.GetAngularVelocity());
					}
					else
					{
						vector3.X *= -1f;
						item.Body.SetTransform(portal2.Destination.GetBody().GetPosition() + vector3, 0f);
					}
					item.BodyData.IncreaseMoveSequence();
					if (GameOwner == GameOwnerEnum.Server)
					{
						AddForcedPositionUpdate(item.BodyData);
					}
					break;
				}
			}
		}
	}

	public void AddForcedPositionUpdate(BodyData bd)
	{
		if (GameOwner != GameOwnerEnum.Server)
		{
			throw new Exception("GameWorld.AddSyncedPositionUpdate is Server Only");
		}
		if (bd.BodyID > 0 && !ObjectForcedPositionUpdates.Contains(bd))
		{
			ObjectForcedPositionUpdates.Add(bd);
		}
	}

	public void AddSyncedPositionUpdate(BodyData bd)
	{
		if (GameOwner != GameOwnerEnum.Server)
		{
			throw new Exception("GameWorld.AddSyncedPositionUpdate is Server Only");
		}
		if (bd.BodyID > 0 && !ObjectSyncedPositionUpdates.Contains(bd))
		{
			ObjectSyncedPositionUpdates.Add(bd);
		}
	}

	public void AddPositionUpdate(ObjectPositionUpdateInfo data)
	{
		if (ObjectPositionUpdates.ContainsKey(data.body))
		{
			ObjectPositionUpdates[data.body].posData.FlagAsFree();
			ObjectPositionUpdates[data.body] = data;
		}
		else
		{
			ObjectPositionUpdates.Add(data.body, data);
		}
	}

	public void RemovePositionUpdate(Body body)
	{
		ObjectPositionUpdates.Remove(body);
	}

	public void HandlePositionUpdates()
	{
		if (ObjectPositionUpdates.Count <= 0)
		{
			return;
		}
		foreach (KeyValuePair<Body, ObjectPositionUpdateInfo> objectPositionUpdate in ObjectPositionUpdates)
		{
			UpdateObjectPosition(objectPositionUpdate.Value.body, objectPositionUpdate.Value.posData);
			objectPositionUpdate.Value.posData.FlagAsFree();
		}
		ObjectPositionUpdates.Clear();
		foreach (KeyValuePair<Player, PlayerSyncTransformData> positionUpdatePlayerSyncTransformDatum in m_positionUpdatePlayerSyncTransformData)
		{
			if (positionUpdatePlayerSyncTransformDatum.Value.DoSync)
			{
				UpdateObjectPositionCheckDoSyncPlayerTransform(positionUpdatePlayerSyncTransformDatum.Key, positionUpdatePlayerSyncTransformDatum.Value);
			}
			positionUpdatePlayerSyncTransformDatum.Value.Free();
		}
		m_positionUpdatePlayerSyncTransformData.Clear();
	}

	public void UpdateObjectPosition(Body body, NetMessage.ObjectPositionUpdate.Data posData)
	{
		if (body == null)
		{
			return;
		}
		if (body.GetUserData() == null)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, "GameWorld.UpdateObjectPosition: Body got null UserData in UpdateObjectPosition, something is removed");
			return;
		}
		BodyData bodyData = BodyData.Read(body);
		bool flag = bodyData.MoveSequenceTAS(posData.MoveSequence);
		Microsoft.Xna.Framework.Vector2 realPosition = posData.Position;
		if (bodyData.IsPlayer && !flag)
		{
			CheckPlayerRelativePositionClipping(bodyData, posData, ref realPosition);
		}
		BodyDataTransformInfo transformInfo = default(BodyDataTransformInfo);
		transformInfo.SetData(body, posData, realPosition);
		Player player = null;
		if (bodyData.IsPlayer)
		{
			player = (Player)bodyData.Object.InternalData;
			if ((player.Climbing && player.ClimbingDirection == 0 && player.Movement == PlayerMovement.Idle && posData.Velocity.LengthSquared() < 0.001f) || flag)
			{
				body.SetTransform(realPosition, 0f);
				if (!flag)
				{
					body.SetAwake(flag: false);
				}
				return;
			}
			if (posData.PlayerSameMovement)
			{
				transformInfo.PositionDiffLength -= Converter.ConvertWorldToBox2D(4f);
				transformInfo.VelocityDiffLength -= Converter.ConvertWorldToBox2D(4f);
			}
			else if (player.Movement == PlayerMovement.Idle)
			{
				transformInfo.PositionDiffLength += Converter.ConvertWorldToBox2D(4f);
				transformInfo.VelocityDiffLength += Converter.ConvertWorldToBox2D(4f);
			}
		}
		else
		{
			bodyData.Object.Health.CurrentValue = posData.ObjectHP;
			bodyData.Object.IgnoreKickVelocityForPlayerID = posData.IgnoreKickVelocityForPlayerID;
			bodyData.Object.FaceDirection = posData.FaceDirection;
			if (posData.IsDynamic && body.GetType() != Box2D.XNA.BodyType.Dynamic)
			{
				body.SetType(Box2D.XNA.BodyType.Dynamic);
				bodyData.Object.CheckDrawFlag();
			}
			else if (!posData.IsDynamic && body.GetType() != Box2D.XNA.BodyType.Static)
			{
				body.SetType(Box2D.XNA.BodyType.Static);
				bodyData.Object.CheckDrawFlag();
			}
		}
		if (!posData.Awake)
		{
			if (body.IsAwake() | (transformInfo.PositionDiffLength > 0.01f) | (transformInfo.AngleDiff > 0.01f) | (transformInfo.AngleDiff < -0.01f))
			{
				if (body.IgnoreNextAngleServerUpdate)
				{
					body.SetTransform(realPosition, body.GetAngle());
					body.IgnoreNextAngleServerUpdate = false;
				}
				else
				{
					body.SetTransform(realPosition, posData.Angle);
					body.SetAngularVelocity(0f);
				}
				body.SetLinearVelocity(Microsoft.Xna.Framework.Vector2.Zero);
				body.SetAwake(flag: false);
				body.VelocityNetworkFactor = 1f;
				if (!flag)
				{
					foreach (Player player2 in Players)
					{
						if (player2.HasLocalControl)
						{
							PlayerSyncTransformData instance = PlayerSyncTransformData.GetInstance();
							UpdateObjectPositionCheckPlayerSyncTransform(body, player2, ref transformInfo, instance);
							if (instance.DoSync)
							{
								m_positionUpdatePlayerSyncTransformData[player2] = instance;
							}
							else
							{
								instance.Free();
							}
						}
					}
				}
			}
		}
		else
		{
			body.VelocityNetworkFactor = 1f;
			if ((transformInfo.PositionDiffLength > Converter.WorldToBox2D(6f)) | (transformInfo.VelocityDiffLength > 4f) | ((transformInfo.VelocityDiffLength < 0.1f) & (transformInfo.AngularVelocityDiff < 0.05f) & (transformInfo.PositionDiffLength > 0.01f)) | (transformInfo.AngleDiff > 0.3f) | (transformInfo.AngleDiff < -0.3f))
			{
				if (body.IgnoreNextAngleServerUpdate)
				{
					body.SetTransform(realPosition, body.GetAngle());
					body.IgnoreNextAngleServerUpdate = false;
				}
				else
				{
					body.SetTransform(realPosition, posData.Angle);
					body.SetAngularVelocity(posData.AngularVelocity);
				}
				body.SetLinearVelocity(posData.Velocity);
				if (!flag)
				{
					foreach (Player player3 in Players)
					{
						if (player3.HasLocalControl)
						{
							PlayerSyncTransformData instance2 = PlayerSyncTransformData.GetInstance();
							UpdateObjectPositionCheckPlayerSyncTransform(body, player3, ref transformInfo, instance2);
							if (instance2.DoSync)
							{
								m_positionUpdatePlayerSyncTransformData[player3] = instance2;
							}
							else
							{
								instance2.Free();
							}
						}
					}
				}
			}
			else if (transformInfo.PositionDiffLength > 0.01f)
			{
				body.SetLinearVelocity(posData.Velocity + transformInfo.PositionDiff * 4f);
				body.SetAngularVelocity(posData.AngularVelocity + transformInfo.AngleDiff * 4f);
				if (bodyData.IsPlayer && transformInfo.PositionDiffLength > 0.04f)
				{
					float val = posData.Velocity.CalcSafeLength() / body.GetLinearVelocity().CalcSafeLength();
					val = Math.Max(0.3f, val);
					val = Math.Min(2f, val);
					body.VelocityNetworkFactor = val;
				}
			}
			if (player != null)
			{
				player.PreBox2DLinearVelocity = body.GetLinearVelocity();
			}
		}
		if (!posData.IsDynamic)
		{
			bodyData.Object.CheckDrawFlag();
		}
		if (posData.ConnectedObjectsPosDataCount <= 0 || posData.ConnectedObjectsPosData == null)
		{
			return;
		}
		for (int i = 0; i < posData.ConnectedObjectsPosDataCount; i++)
		{
			NetMessage.ObjectPositionUpdate.Data data = posData.ConnectedObjectsPosData[i];
			BodyData bodyDataByID = GetBodyDataByID(data.ID);
			if (bodyDataByID != null && bodyDataByID.MessageIsNewer(data.MessageCount, TAS: true))
			{
				UpdateObjectPosition(bodyDataByID.Owner, data);
			}
		}
	}

	public void CheckPlayerRelativePositionClipping(BodyData playerBodyData, NetMessage.ObjectPositionUpdate.Data posData, ref Microsoft.Xna.Framework.Vector2 realPosition)
	{
		Player player = (Player)playerBodyData.Object.InternalData;
		Body owner = playerBodyData.Owner;
		if (posData.RelativePositionData.Down.BodyID != 0)
		{
			BodyData bodyDataByID = GetBodyDataByID(posData.RelativePositionData.Down.BodyID);
			if (bodyDataByID != null && bodyDataByID.Owner != null && bodyDataByID.Object != null && !bodyDataByID.Object.IsDisposed && bodyDataByID.MoveSequence == posData.RelativePositionData.Down.MoveSequence)
			{
				float rotation = bodyDataByID.Owner.GetAngle();
				if (Math.Abs(bodyDataByID.Owner.GetAngle() - posData.RelativePositionData.Down.BodyAngle) > (float)Math.PI / 4f)
				{
					rotation = posData.RelativePositionData.Down.BodyAngle;
				}
				SFDMath.RotatePosition(ref posData.RelativePositionData.Down.Position, rotation, out posData.RelativePositionData.Down.Position);
				Microsoft.Xna.Framework.Vector2 vector = realPosition;
				Microsoft.Xna.Framework.Vector2 vector2 = bodyDataByID.Owner.GetPosition() + posData.RelativePositionData.Down.Position;
				realPosition = vector2 + (vector - vector2) * posData.RelativePositionData.Down.Fraction;
				if (player.CheckIgnoreStandingOnBodyVelocity(bodyDataByID.Object))
				{
					realPosition.X = vector.X;
				}
			}
			else
			{
				realPosition = owner.GetPosition();
			}
			CheckPlayerRelativePositionClippingDirection(ref realPosition, ref posData.RelativePositionData.Down, CheckRelativePositionClippingDirectionEnum.Down);
		}
		if (posData.RelativePositionData.Up.BodyID != 0)
		{
			CheckPlayerRelativePositionClippingDirection(ref realPosition, ref posData.RelativePositionData.Up, CheckRelativePositionClippingDirectionEnum.Up);
		}
		if (posData.RelativePositionData.Left.BodyID != 0)
		{
			CheckPlayerRelativePositionClippingDirection(ref realPosition, ref posData.RelativePositionData.Left, CheckRelativePositionClippingDirectionEnum.Left);
		}
		if (posData.RelativePositionData.Right.BodyID != 0)
		{
			CheckPlayerRelativePositionClippingDirection(ref realPosition, ref posData.RelativePositionData.Right, CheckRelativePositionClippingDirectionEnum.Right);
		}
	}

	public void CheckPlayerRelativePositionClippingDirection(ref Microsoft.Xna.Framework.Vector2 realPosition, ref NetMessage.ObjectPositionUpdate.Data.RelativeData relativePositionData, CheckRelativePositionClippingDirectionEnum checkRelativePositionClippingDirection)
	{
		BodyData bodyDataByID = GetBodyDataByID(relativePositionData.BodyID);
		if (bodyDataByID != null && bodyDataByID.Owner != null && bodyDataByID.Object != null && !bodyDataByID.Object.IsDisposed && bodyDataByID.MoveSequence == relativePositionData.MoveSequence)
		{
			CheckRelativePositionClipping(ref realPosition, bodyDataByID.Owner, ref relativePositionData, checkRelativePositionClippingDirection);
		}
	}

	public void CheckRelativePositionClipping(ref Microsoft.Xna.Framework.Vector2 realPosition, Body body, ref NetMessage.ObjectPositionUpdate.Data.RelativeData relativePositionData, CheckRelativePositionClippingDirectionEnum checkRelativePositionClippingDirection)
	{
		Fixture fixtureByIndex = body.GetFixtureByIndex(relativePositionData.TileFixtureIndex);
		if (fixtureByIndex == null)
		{
			return;
		}
		Box2D.XNA.RayCastInput input = new Box2D.XNA.RayCastInput
		{
			maxFraction = 1f
		};
		Microsoft.Xna.Framework.Vector2 position = Microsoft.Xna.Framework.Vector2.Zero;
		switch (checkRelativePositionClippingDirection)
		{
		case CheckRelativePositionClippingDirectionEnum.Down:
			position = -Microsoft.Xna.Framework.Vector2.UnitY;
			break;
		case CheckRelativePositionClippingDirectionEnum.Up:
			position = Microsoft.Xna.Framework.Vector2.UnitY;
			break;
		case CheckRelativePositionClippingDirectionEnum.Left:
			position = -Microsoft.Xna.Framework.Vector2.UnitX;
			break;
		case CheckRelativePositionClippingDirectionEnum.Right:
			position = Microsoft.Xna.Framework.Vector2.UnitX;
			break;
		}
		float rotation = body.GetAngle() - relativePositionData.BodyAngle;
		SFDMath.RotatePosition(ref position, rotation, out position);
		input.p1 = realPosition - position * 0.04f * 100f;
		input.p2 = realPosition + position * 0.04f * 100f;
		if (!fixtureByIndex.RayCast(out var output, ref input))
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 hitPosition = input.GetHitPosition(output.fraction);
		switch (checkRelativePositionClippingDirection)
		{
		case CheckRelativePositionClippingDirectionEnum.Down:
			hitPosition.Y += 0.16f;
			if (realPosition.Y < hitPosition.Y)
			{
				realPosition.Y = hitPosition.Y;
			}
			break;
		case CheckRelativePositionClippingDirectionEnum.Up:
			hitPosition.Y -= 0.16f;
			if (realPosition.Y > hitPosition.Y)
			{
				realPosition.Y = hitPosition.Y;
			}
			break;
		case CheckRelativePositionClippingDirectionEnum.Left:
			hitPosition.X += 0.16f;
			if (realPosition.X < hitPosition.X)
			{
				realPosition.X = hitPosition.X;
			}
			break;
		case CheckRelativePositionClippingDirectionEnum.Right:
			hitPosition.X -= 0.16f;
			if (realPosition.X > hitPosition.X)
			{
				realPosition.X = hitPosition.X;
			}
			break;
		}
	}

	public void UpdateObjectPositionCheckPlayerSyncTransform(Body body, Player player, ref BodyDataTransformInfo transformInfo, PlayerSyncTransformData playerSyncTransformData)
	{
		playerSyncTransformData.Position = Microsoft.Xna.Framework.Vector2.Zero;
		playerSyncTransformData.Velocity = Microsoft.Xna.Framework.Vector2.Zero;
		playerSyncTransformData.Body = null;
		playerSyncTransformData.DoSync = false;
		if (GameOwner == GameOwnerEnum.Server || body.GetType() == Box2D.XNA.BodyType.Static || player == null || player.IsRemoved || (Math.Abs(transformInfo.AngleDiff) < 0.01f && transformInfo.PositionDiff.CalcSafeLength() < 0.01f))
		{
			return;
		}
		bool flag = false;
		Filter filterA = player.CollisionFilter;
		Filter filter;
		for (Fixture fixture = body.GetFixtureList(); fixture != null; fixture = fixture.GetNext())
		{
			if (!fixture.IsSensor())
			{
				fixture.GetFilterData(out filter);
				if (Settings.b2ShouldCollide(ref filterA, ref filter))
				{
					flag = true;
					break;
				}
			}
		}
		if (!flag)
		{
			return;
		}
		Microsoft.Xna.Framework.Vector2 position = body.GetPosition();
		Microsoft.Xna.Framework.Vector2 point = player.WorldBody.GetPosition();
		if (player.StandingOnBody == body)
		{
			ObjectData objectData = ObjectData.Read(body);
			float rotation = body.GetAngle() - transformInfo.OriginalAngle;
			if (objectData != null && objectData.ClientSyncDisableAnglePositionClippingCheck)
			{
				rotation = 0f;
			}
			Microsoft.Xna.Framework.Vector2 position2 = point - transformInfo.OriginalPosition;
			SFDMath.RotatePosition(ref position2, rotation, out var rotatedPosition);
			playerSyncTransformData.Position = position + rotatedPosition;
			playerSyncTransformData.Velocity = body.GetLinearVelocityFromWorldPoint(playerSyncTransformData.Position);
			if (player.CheckIgnoreStandingOnBodyVelocity(objectData))
			{
				playerSyncTransformData.Position.X = player.WorldBody.GetPosition().X;
				playerSyncTransformData.Velocity.X = player.WorldBody.GetLinearVelocity().X;
			}
			playerSyncTransformData.Body = body;
			playerSyncTransformData.DoSync = true;
			return;
		}
		Microsoft.Xna.Framework.Vector2 growth = transformInfo.OriginalPosition - position;
		body.GetAABB(out var aabb);
		aabb.Grow((aabb.upperBound - aabb.lowerBound).Length());
		aabb.Grow(ref growth);
		aabb.Grow(0.16f);
		aabb.Grow(0.01f);
		if (aabb.Contains(ref point))
		{
			ObjectData objectData2 = ObjectData.Read(body);
			float num = body.GetAngle() - transformInfo.OriginalAngle;
			if (objectData2 != null && objectData2.ClientSyncDisableAnglePositionClippingCheck)
			{
				num = 0f;
			}
			Microsoft.Xna.Framework.Vector2 position3 = point - transformInfo.OriginalPosition;
			SFDMath.RotatePosition(ref position3, num, out var rotatedPosition2);
			Microsoft.Xna.Framework.Vector2 vector = position + rotatedPosition2;
			Microsoft.Xna.Framework.Vector2 position4 = point - vector;
			SFDMath.RotatePosition(ref position4, 0f - num, out position4);
			Box2D.XNA.RayCastInput input = default(Box2D.XNA.RayCastInput);
			float num2 = 1f;
			Microsoft.Xna.Framework.Vector2 vector2 = Microsoft.Xna.Framework.Vector2.Zero;
			input.maxFraction = 1f;
			input.p1 = vector;
			input.p2 = vector + position4;
			Microsoft.Xna.Framework.Vector2 vector3 = Microsoft.Xna.Framework.Vector2.Normalize(input.p2 - input.p1);
			input.p2 += vector3 * 0.16f;
			for (Fixture fixture2 = body.GetFixtureList(); fixture2 != null; fixture2 = fixture2.GetNext())
			{
				if (!fixture2.IsSensor())
				{
					fixture2.GetFilterData(out filter);
					if (Settings.b2ShouldCollide(ref filterA, ref filter) && fixture2.RayCast(out var output, ref input) && num2 > output.fraction)
					{
						num2 = output.fraction;
						vector2 = input.GetHitPosition(num2);
					}
				}
			}
			if (num2 < 1f)
			{
				playerSyncTransformData.Position = vector2 - vector3 * 0.16f;
				playerSyncTransformData.Velocity = body.GetLinearVelocityFromWorldPoint(playerSyncTransformData.Position);
				if (player.CheckIgnoreStandingOnBodyVelocity(objectData2))
				{
					playerSyncTransformData.Position.X = player.WorldBody.GetPosition().X;
					playerSyncTransformData.Velocity.X = player.WorldBody.GetLinearVelocity().X;
				}
				playerSyncTransformData.Body = body;
				playerSyncTransformData.DoSync = true;
				return;
			}
		}
		playerSyncTransformData.Position = Microsoft.Xna.Framework.Vector2.Zero;
		playerSyncTransformData.Velocity = Microsoft.Xna.Framework.Vector2.Zero;
		playerSyncTransformData.Body = null;
		playerSyncTransformData.DoSync = false;
	}

	public void UpdateObjectPositionCheckDoSyncPlayerTransform(Player player, PlayerSyncTransformData playerTransformData)
	{
		if (player.IsRemoved || (playerTransformData.Position - player.WorldBody.GetPosition()).CalcSafeLength() < 0.02f)
		{
			return;
		}
		Box2D.XNA.RayCastInput rci = default(Box2D.XNA.RayCastInput);
		rci.p1 = player.WorldBody.GetPosition();
		rci.p2 = playerTransformData.Position;
		rci.maxFraction = 1f;
		Body ignoreBody = playerTransformData.Body;
		Filter filterPlayer = ((Player)player.ObjectData.InternalData).CollisionFilter;
		Filter filterTestFixture;
		player.WorldBody.GetWorld().RayCast(delegate(Fixture testFixture, Microsoft.Xna.Framework.Vector2 point, Microsoft.Xna.Framework.Vector2 normal, float rayFraction)
		{
			if (testFixture.IsSensor() | (ignoreBody == testFixture.GetBody()) | (rayFraction > rci.maxFraction))
			{
				return 1f;
			}
			testFixture.GetFilterData(out filterTestFixture);
			if (Settings.b2ShouldCollide(ref filterPlayer, ref filterTestFixture))
			{
				rci.maxFraction = rayFraction;
				return rayFraction;
			}
			return 1f;
		}, rci.p1, rci.p2);
		Microsoft.Xna.Framework.Vector2 hitPosition = rci.GetHitPosition(rci.maxFraction);
		if (rci.maxFraction < 1f)
		{
			Microsoft.Xna.Framework.Vector2 vector = Microsoft.Xna.Framework.Vector2.Normalize(rci.p2 - rci.p1);
			if (vector.IsValid())
			{
				hitPosition -= vector * 0.16f * 0.25f;
			}
		}
		player.WorldBody.SetTransform(hitPosition, 0f);
	}

	public int GetNextProjectileWorldId()
	{
		m_projectileWorldId++;
		return m_projectileWorldId;
	}

	public void HandleProjectileUpdate(ref NetMessage.ProjectileUpdate.Data[] data, int count, GameUser[] senderGameUsers = null)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			HandleProjectileUpdateClient(ref data, count);
		}
		else if (GameOwner == GameOwnerEnum.Server)
		{
			HandleProjectileUpdateServer(ref data, count, senderGameUsers);
		}
		else
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"{GameOwner}: Unhandled GameOwner for GameWorld.HandleProjectileUpdate()");
		}
	}

	public void HandleProjectileUpdateServer(ref NetMessage.ProjectileUpdate.Data[] data, int count, GameUser[] senderGameUsers)
	{
		if (GameOwner != GameOwnerEnum.Server)
		{
			throw new Exception("Only server is allowed to call GameWorld.HandleProjectileUpdateServer()");
		}
		for (int i = 0; i < count; i++)
		{
			NetMessage.ProjectileUpdate.Data data2 = data[i];
			if (data2.Remove)
			{
				continue;
			}
			Projectile projectile = GetProjectile(data2.WorldID);
			if (projectile == null || !projectile.NetMessageClient.MessageIsNewer(data2.MessageCount, TAS: true) || projectile.Properties.ProjectileID != 17)
			{
				continue;
			}
			ProjectileBazooka projectileBazooka = (ProjectileBazooka)projectile;
			Player plr = projectileBazooka.GetRocketRidePlayer();
			if (plr != null && senderGameUsers.Any((GameUser x) => x.UserIdentifier == plr.UserIdentifier))
			{
				float num = Microsoft.Xna.Framework.Vector2.Dot(projectile.Direction, data2.Direction);
				projectile.Velocity = data2.Velocity;
				projectile.TotalDistanceTraveled = data2.DistanceTraveled;
				Microsoft.Xna.Framework.Vector2 vector = data2.Position - projectile.Position;
				float num2 = projectile.GetSpeed() * 0.05f;
				float num3 = vector.Length();
				vector.Normalize();
				float value = 1f;
				if (vector.IsValid())
				{
					value = Microsoft.Xna.Framework.Vector2.Dot(data2.Direction, vector);
				}
				if (!(num3 > num2) && !(num3 < 1f) && !(num < 0.98f) && Math.Abs(value) > 0.7f)
				{
					float value2 = Microsoft.Xna.Framework.Vector2.Dot(vector, projectile.Direction);
					float num4 = projectile.GetSpeed() + num3 * (float)Math.Sign(value2) * 1.3333334f;
					projectile.CatchUpSpeedModifier = num4 / projectile.Properties.InitialSpeed;
				}
				else
				{
					projectile.Position = data2.Position;
					projectile.CatchUpSpeedModifier = 1f;
				}
			}
			else
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Information, string.Format("Server: ", Array.Empty<object>()));
			}
		}
	}

	public void HandleProjectileUpdateClient(ref NetMessage.ProjectileUpdate.Data[] data, int count)
	{
		if (GameOwner != GameOwnerEnum.Client)
		{
			throw new Exception("Only client is allowed to call GameWorld.HandleProjectileUpdateClient()");
		}
		List<Player> list = new List<Player>();
		for (int i = 0; i < count; i++)
		{
			NetMessage.ProjectileUpdate.Data data2 = data[i];
			if (data2.Remove)
			{
				Projectile projectile = GetProjectile(data2.WorldID);
				if (projectile == null)
				{
					OldProjectiles.Add(data2.WorldID);
					continue;
				}
				UpdateProjectile(projectile, 0.015f);
				projectile.Position = data2.Position;
				if (projectile.HitObjectID != data2.HitObjectID)
				{
					ObjectData objectDataByID = GetObjectDataByID(data2.HitObjectID);
					if (objectDataByID != null)
					{
						if (!objectDataByID.IsPlayer)
						{
							projectile.HitNormal = data2.HitNormal;
							projectile.HitObjectID = data2.HitObjectID;
							projectile.HitFixtureIndex = data2.HitFixtureIndex;
							Fixture fixtureByIndex = objectDataByID.GetFixtureByIndex(data2.HitFixtureIndex);
							projectile.HitFixtureID = ((fixtureByIndex != null) ? fixtureByIndex.ID : "");
							ProjectileHitEventArgs e = new ProjectileHitEventArgs(fixtureByIndex, objectDataByID, customHandled: false, stopProjectileProgress: false);
							projectile.BeforeHitObject(e);
							if (!e.CustomHandled)
							{
								objectDataByID.ProjectileHit(projectile, e);
								projectile.HitObject(objectDataByID, e);
							}
						}
					}
					else
					{
						ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, "Client: Failed to simulate projectile hit correctly - could not find object hit");
					}
				}
				projectile.HitFlag = true;
				RemoveProjectile(projectile);
				continue;
			}
			Projectile projectile2 = GetProjectile(data2.WorldID);
			if (projectile2 == null)
			{
				RWeapon firedFromWeapon = GetPlayer(data2.PlayerOwnerID)?.GetCurrentRangedWeaponInUse();
				float num = ((data2.DistanceTraveled < 32f) ? data2.DistanceTraveled : 32f);
				projectile2 = CreateProjectile(data2.ProjectileID, firedFromWeapon, data2.WorldID, data2.Position - data2.Direction * (data2.DistanceTraveled - num), data2.Direction, num, data2.PlayerOwnerID);
				projectile2.NextDeflectionValue = data2.NextDeflectionValue;
				projectile2.MessageCountReceive = data2.MessageCount;
				projectile2.ReadAdditionalData(data2.AdditionalData);
				projectile2.PowerupFireActive = data2.PowerupFireActive;
				projectile2.PowerupBounceActive = data2.PowerupBounceActive;
				projectile2.PowerupBouncesDone = data2.PowerupBouncesDone;
				if (projectile2.PlayerOwner != null && !list.Contains(projectile2.PlayerOwner))
				{
					list.Add(projectile2.PlayerOwner);
				}
				RWeapon rWeapon = null;
				if (projectile2.PlayerOwner != null)
				{
					rWeapon = ((projectile2.PlayerOwner.CurrentWeaponDrawn != SFD.Weapons.WeaponItemType.Handgun) ? projectile2.PlayerOwner.CurrentRifleWeapon : projectile2.PlayerOwner.CurrentHandgunWeapon);
				}
				if (rWeapon != null)
				{
					projectile2.CheckTunneling = true;
					projectile2.TunnelingPosition = projectile2.Position - projectile2.Direction * (rWeapon.Properties.MuzzlePosition.X + projectile2.PlayerOwner.GetRangedFireArmLength());
				}
			}
			else if (projectile2.MessageIsNewer(data2.MessageCount, TAS: true) && !projectile2.HandleDataMessage(data2))
			{
				float num2 = Microsoft.Xna.Framework.Vector2.Dot(projectile2.Direction, data2.Direction);
				projectile2.PlayerOwnerID = data2.PlayerOwnerID;
				projectile2.ObjectIDToIgnore = data2.ObjectIDToIgnore;
				projectile2.Velocity = data2.Velocity;
				projectile2.TotalDistanceTraveled = data2.DistanceTraveled;
				projectile2.NextDeflectionValue = data2.NextDeflectionValue;
				projectile2.HitFlag = false;
				projectile2.ReadAdditionalData(data2.AdditionalData);
				projectile2.PowerupFireActive = data2.PowerupFireActive;
				projectile2.PowerupBounceActive = data2.PowerupBounceActive;
				projectile2.PowerupBouncesDone = data2.PowerupBouncesDone;
				Microsoft.Xna.Framework.Vector2 vector = data2.Position - projectile2.Position;
				float num3 = projectile2.GetSpeed() * 0.05f;
				float num4 = vector.Length();
				vector.Normalize();
				float value = 1f;
				if (vector.IsValid())
				{
					value = Microsoft.Xna.Framework.Vector2.Dot(data2.Direction, vector);
				}
				if (!(num4 > num3) && !(num4 < 1f) && !(num2 < 0.98f) && Math.Abs(value) > 0.7f)
				{
					float value2 = Microsoft.Xna.Framework.Vector2.Dot(vector, projectile2.Direction);
					float num5 = projectile2.GetSpeed() + num4 * (float)Math.Sign(value2) * 1.3333334f;
					projectile2.CatchUpSpeedModifier = num5 / projectile2.Properties.InitialSpeed;
				}
				else
				{
					projectile2.Position = data2.Position;
					projectile2.CatchUpSpeedModifier = 1f;
				}
			}
		}
		if (list.Count > 0)
		{
			foreach (Player item in list)
			{
				item.ShowRangedWeaponFireRecoil();
				if (!BringPlayerToFront.Contains(item))
				{
					BringPlayerToFront.Add(item);
				}
			}
		}
		list.Clear();
		list = null;
	}

	public Projectile GetProjectile(int worldId)
	{
		foreach (Projectile projectile in Projectiles)
		{
			if (projectile.InstanceID == worldId)
			{
				return projectile;
			}
		}
		foreach (Projectile newProjectile in NewProjectiles)
		{
			if (newProjectile.InstanceID == worldId)
			{
				return newProjectile;
			}
		}
		return null;
	}

	public bool RemoveProjectile(Projectile projectile)
	{
		int num = 0;
		while (true)
		{
			if (num < Projectiles.Count)
			{
				if (Projectiles[num] == projectile)
				{
					break;
				}
				num++;
				continue;
			}
			int num2 = 0;
			while (true)
			{
				if (num2 < NewProjectiles.Count)
				{
					if (NewProjectiles[num2] == projectile)
					{
						break;
					}
					num2++;
					continue;
				}
				return false;
			}
			projectile.RemovedFlag = true;
			NewProjectiles.RemoveAt(num2);
			return true;
		}
		projectile.RemovedFlag = true;
		Projectiles.RemoveAt(num);
		return true;
	}

	public bool RemoveProjectile(int worldId)
	{
		int num = 0;
		while (true)
		{
			if (num < Projectiles.Count)
			{
				if (Projectiles[num].InstanceID == worldId)
				{
					break;
				}
				num++;
				continue;
			}
			int num2 = 0;
			while (true)
			{
				if (num2 < NewProjectiles.Count)
				{
					if (NewProjectiles[num2].InstanceID == worldId)
					{
						break;
					}
					num2++;
					continue;
				}
				return false;
			}
			NewProjectiles[num2].RemovedFlag = true;
			NewProjectiles.RemoveAt(num2);
			return true;
		}
		Projectiles[num].RemovedFlag = true;
		Projectiles.RemoveAt(num);
		return true;
	}

	public void InitializeProjectile(Projectile projectile, RWeapon firedFromWeapon, int worldId, Microsoft.Xna.Framework.Vector2 worldPosition, Microsoft.Xna.Framework.Vector2 direction, float distanceTraveled, int ownerPlayerId)
	{
		projectile.GameWorld = this;
		projectile.Position = worldPosition;
		projectile.Velocity = direction * projectile.Properties.InitialSpeed;
		projectile.TotalDistanceTraveled = distanceTraveled;
		projectile.PlayerOwnerID = ownerPlayerId;
		projectile.InitialPlayerOwnerID = ownerPlayerId;
		projectile.InstanceID = worldId;
		projectile.NextDeflectionValue = Constants.RANDOM.NextFloat();
		projectile.PowerupBounceActive = false;
		projectile.PowerupFireActive = false;
		Player playerOwner = projectile.PlayerOwner;
		if (playerOwner != null)
		{
			if (playerOwner.CoverObject != null && playerOwner.CoverObjectCanShootThrough)
			{
				projectile.ObjectIDToIgnore = playerOwner.CoverObject.ObjectID;
			}
			PlayerModifiers modifiers = playerOwner.GetModifiers();
			projectile.CritChanceDealtModifier = modifiers.ProjectileCritChanceDealtModifier;
			projectile.DamageDealtModifier = modifiers.ProjectileDamageDealtModifier;
		}
		projectile.FiredByWeapon(firedFromWeapon);
		projectile.AfterCreated();
	}

	public Projectile CreateProjectile(short projectileId, RWeapon firedFromWeapon, Microsoft.Xna.Framework.Vector2 worldPosition, Microsoft.Xna.Framework.Vector2 direction, int ownerPlayerId)
	{
		return CreateProjectile(projectileId, firedFromWeapon, GetNextProjectileWorldId(), worldPosition, direction, 0f, ownerPlayerId);
	}

	public Projectile CreateProjectile(short projectileId, RWeapon firedFromWeapon, int worldId, Microsoft.Xna.Framework.Vector2 worldPosition, Microsoft.Xna.Framework.Vector2 direction, float distanceTraveled, int ownerPlayerId)
	{
		Projectile projectile = ProjectileDatabase.GetProjectile(projectileId);
		if (projectile == null)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, "GameWorld.CreateProjectile: Couldn't find projectile with Id " + projectileId);
			return null;
		}
		projectile = projectile.Copy();
		InitializeProjectile(projectile, firedFromWeapon, worldId, worldPosition, direction, distanceTraveled, ownerPlayerId);
		NewProjectiles.Add(projectile);
		return projectile;
	}

	public Projectile CreateProjectile(Projectile projectileToCopy, RWeapon firedFromWeapon, Microsoft.Xna.Framework.Vector2 worldPosition, Microsoft.Xna.Framework.Vector2 direction, int ownerPlayerId)
	{
		return CreateProjectile(projectileToCopy, firedFromWeapon, GetNextProjectileWorldId(), worldPosition, direction, 0f, ownerPlayerId);
	}

	public Projectile CreateProjectile(Projectile projectileToCopy, RWeapon firedFromWeapon, int worldId, Microsoft.Xna.Framework.Vector2 worldPosition, Microsoft.Xna.Framework.Vector2 direction, float distanceTraveled, int ownerPlayerId)
	{
		if (projectileToCopy == null)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, "GameWorld.CreateProjectile: Couldn't create projectile, projectileToCopy is null");
			return null;
		}
		Projectile projectile = projectileToCopy.Copy();
		InitializeProjectile(projectile, firedFromWeapon, worldId, worldPosition, direction, distanceTraveled, ownerPlayerId);
		NewProjectiles.Add(projectile);
		return projectile;
	}

	public Projectile SpawnProjectile(short projectileId, Microsoft.Xna.Framework.Vector2 worldPosition, Microsoft.Xna.Framework.Vector2 direction, int objectIDToIgnore = 0, ProjectilePowerup powerup = ProjectilePowerup.None)
	{
		Projectile projectile = ProjectileDatabase.GetProjectile(projectileId);
		if (projectile == null)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, "GameWorld.SpawnProjectile: Couldn't find projectile with Id " + projectileId);
			return null;
		}
		direction.Normalize();
		if (!direction.IsValid())
		{
			direction = Microsoft.Xna.Framework.Vector2.UnitX;
		}
		projectile = projectile.Copy();
		InitializeProjectile(projectile, null, GetNextProjectileWorldId(), worldPosition, direction, 0f, 0);
		projectile.ObjectIDToIgnore = objectIDToIgnore;
		switch (powerup)
		{
		case ProjectilePowerup.Fire:
			projectile.PowerupFireActive = true;
			break;
		case ProjectilePowerup.Bouncing:
			projectile.PowerupBounceActive = true;
			break;
		}
		NewProjectiles.Add(projectile);
		if (GameOwner == GameOwnerEnum.Server)
		{
			NetMessage.ProjectileUpdate.Data[] dataToWrite = new NetMessage.ProjectileUpdate.Data[1]
			{
				new NetMessage.ProjectileUpdate.Data()
			};
			dataToWrite[0].SetData(projectile, projectile.MessageCountSend);
			NetMessage.ProjectileUpdate.Write(ref dataToWrite, 1, GetMultiPacket());
		}
		return projectile;
	}

	public void ResetPlayerProjectHitTest(Projectile projectile)
	{
		for (int i = 0; i < Players.Count; i++)
		{
			Players[i].ResetProjectileHitTest(projectile);
		}
	}

	public void UpdateProjectile(Projectile projectile, float elapsedSeconds)
	{
		elapsedSeconds *= projectile.SlowmotionFactor;
		Microsoft.Xna.Framework.Vector2 position = projectile.Position;
		if (!projectile.HitFlag)
		{
			if (projectile.CheckTunneling)
			{
				CheckProjectileTunneling(projectile);
			}
			else
			{
				projectile.Update(elapsedSeconds * 1000f);
			}
			elapsedSeconds *= projectile.GetSpeedModifier();
			float num = projectile.GetSpeed() * elapsedSeconds * projectile.CatchUpSpeedModifier;
			Microsoft.Xna.Framework.Vector2 vector = projectile.Position + projectile.Velocity * elapsedSeconds;
			projectile.GetRayCastInput(out var rayCastInput, elapsedSeconds);
			List<KeyValuePair<RayCastOutput, Fixture>> list = CheckProjectileHit(projectile, ref rayCastInput, environmentCollision: true, playerCollision: true, includeOverlappingFixture: true, TunnelingCheckType.None);
			float totalDistanceTraveled = projectile.TotalDistanceTraveled + num;
			float visualDistanceTraveled = projectile.VisualDistanceTraveled + num;
			float playerDistanceTraveled = projectile.PlayerDistanceTraveled + num;
			Microsoft.Xna.Framework.Vector2 position2 = vector;
			KeyValuePair<RayCastOutput, Fixture>? keyValuePair = null;
			int num2 = -1;
			bool stopProjectileUpdateProgress = false;
			foreach (KeyValuePair<RayCastOutput, Fixture> item in list)
			{
				num2++;
				float fraction = item.Key.fraction;
				vector = (1f - fraction) * rayCastInput.p1 + fraction * rayCastInput.p2;
				vector = Converter.Box2DToWorld(vector);
				num = (vector - projectile.Position).Length();
				projectile.TotalDistanceTraveled += num;
				projectile.VisualDistanceTraveled += num;
				projectile.PlayerDistanceTraveled += num;
				projectile.Position = vector;
				if (projectile.CheckPreviouslyOverlapFixtures(item.Value, item.Key.normal))
				{
					continue;
				}
				bool flag = false;
				ObjectData objectData = ObjectData.Read(item.Value);
				if (objectData.ObjectID == projectile.HitObjectID)
				{
					continue;
				}
				Fixture value = item.Value;
				projectile.HitObjectID = objectData.ObjectID;
				projectile.HitNormal = item.Key.normal;
				projectile.HitFixtureID = item.Value.ID;
				projectile.HitFixtureIndex = objectData.GetFixtureIndex(item.Value);
				projectile.HitDamageCrit = false;
				projectile.HitDamageValue = 0f;
				if (objectData.IsPlayer)
				{
					Player player = (Player)objectData.InternalData;
					if (!player.CheckProjectileDeflection(projectile, position))
					{
						projectile.HitFlag = true;
						projectile.HitPlayer(player, objectData);
						CheckProjectileFirePowerupEffect(projectile, objectData);
						if (projectile.HitFlag)
						{
							UpdateProjectileRunScripts(projectile, objectData, Microsoft.Xna.Framework.Vector2.Zero, ref stopProjectileUpdateProgress);
							break;
						}
						continue;
					}
					stopProjectileUpdateProgress = true;
					break;
				}
				bool flag2 = true;
				if (keyValuePair.HasValue && keyValuePair.Value.Value.GetBody().GetType() == Box2D.XNA.BodyType.Static && item.Value.GetBody().GetType() == Box2D.XNA.BodyType.Static)
				{
					float fraction2 = keyValuePair.Value.Key.fraction;
					float num3 = item.Key.fraction - fraction2;
					if (num * num3 < 20f)
					{
						ObjectData objectData2 = ObjectData.Read(keyValuePair.Value.Value);
						if (objectData2.DoMergeProjectileHitsForStaticTiles && objectData2.Tile.Key == objectData.Tile.Key && objectData2.Tile.GetTileFixtureMaterial(keyValuePair.Value.Value.TileFixtureIndex) == objectData.Tile.GetTileFixtureMaterial(value.TileFixtureIndex))
						{
							flag2 = false;
						}
					}
				}
				keyValuePair = item;
				if (num2 < projectile.PreviouslyOverlapFixtures.Length)
				{
					projectile.PreviouslyOverlapFixtures[num2] = new Projectile.PrevHitData(item.Value, projectile.Direction, projectile.TotalDistanceTraveled);
				}
				else
				{
					projectile.PreviouslyOverlapFixtures[0] = new Projectile.PrevHitData(item.Value, projectile.Direction, projectile.TotalDistanceTraveled);
				}
				if (!flag2)
				{
					continue;
				}
				ProjectileHitEventArgs e = new ProjectileHitEventArgs(value, objectData, customHandled: false, stopProjectileProgress: false);
				projectile.BeforeHitObject(e);
				if (!e.CustomHandled)
				{
					objectData.ProjectileHit(projectile, e);
					if (!e.CustomHandled)
					{
						if (item.Value.ObjectStrength != 0f)
						{
							projectile.StrengthLeft -= item.Value.ObjectStrength;
							if (projectile.StrengthLeft > 0f && objectData.Destructable)
							{
								if (GameOwner != GameOwnerEnum.Client)
								{
									projectile.HitDamageValue = Math.Max(projectile.HitDamageValue, objectData.Health.CurrentValue);
									objectData.Destroy();
									flag = true;
								}
							}
							else if (item.Value.AbsorbProjectile)
							{
								projectile.HitFlag = true;
								flag = true;
							}
						}
						else if (item.Value.AbsorbProjectile)
						{
							projectile.HitFlag = true;
							flag = true;
						}
					}
					if (projectile.HitFlag)
					{
						CheckProjectileFirePowerupEffect(projectile, e.HitObject);
						if (projectile.PowerupBounceActive && e.HitObject != null && !e.HitObject.TerminationInitiated && !e.HitObject.IsPlayer && projectile.HitFlag && projectile.PowerupBouncesDone < projectile.Properties.PowerupTotalBounces)
						{
							e.ReflectionStatus = ProjectileReflectionStatus.WillBeReflected;
							projectile.HitObject(objectData, e);
							Microsoft.Xna.Framework.Vector2 vector2 = Microsoft.Xna.Framework.Vector2.Zero;
							if (e.ReflectionStatus == ProjectileReflectionStatus.WillBeReflected)
							{
								projectile.PowerupBouncesDone++;
								vector2 = Microsoft.Xna.Framework.Vector2.Reflect(projectile.Direction, projectile.HitNormal);
								if (Microsoft.Xna.Framework.Vector2.Dot(projectile.HitNormal, vector2) > 0.1f)
								{
									Random random = new Random(projectile.InstanceID * 17 + e.HitObject.ObjectID * 1013 + ((projectile.InstanceID % 2 == 0) ? 1000000 : 0));
									Microsoft.Xna.Framework.Vector2 position3 = vector2;
									float num4 = ((float)random.NextDouble() - 0.5f) * 2f;
									num4 *= Microsoft.Xna.Framework.MathHelper.ToRadians(projectile.Properties.PowerupBounceRandomAngle);
									SFDMath.RotatePosition(ref position3, num4, out position3);
									vector2 = position3;
								}
							}
							if (UpdateProjectileRunScripts(projectile, objectData, vector2, ref stopProjectileUpdateProgress))
							{
								e.StopProjectileFrameProgress = stopProjectileUpdateProgress;
							}
							if (e.ReflectionStatus == ProjectileReflectionStatus.WillBeReflected)
							{
								if (!stopProjectileUpdateProgress)
								{
									projectile.HitFlag = false;
									projectile.VisualDistanceTraveled = 4f;
									projectile.Position -= projectile.Direction * 0.5f - projectile.HitNormal * 0.5f;
									projectile.Direction = vector2;
									e.StopProjectileFrameProgress = true;
								}
								projectile.PlayerOwnerID = 0;
								projectile.PlayerDistanceTraveled = 0f;
								projectile.HitObjectID = 0;
								projectile.HitNormal = Microsoft.Xna.Framework.Vector2.Zero;
								projectile.HitFixtureID = "";
								projectile.HitFixtureIndex = -1;
								projectile.ObjectIDToIgnore = 0;
								ResetPlayerProjectHitTest(projectile);
							}
						}
						else
						{
							projectile.HitObject(objectData, e);
							if (UpdateProjectileRunScripts(projectile, objectData, Microsoft.Xna.Framework.Vector2.Zero, ref stopProjectileUpdateProgress))
							{
								e.StopProjectileFrameProgress = stopProjectileUpdateProgress;
							}
						}
						stopProjectileUpdateProgress = e.StopProjectileFrameProgress;
						break;
					}
					if (!e.CustomHandled)
					{
						projectile.HitObject(objectData, e);
						if (projectile.HitFlag)
						{
							UpdateProjectileRunScripts(projectile, objectData, Microsoft.Xna.Framework.Vector2.Zero, ref stopProjectileUpdateProgress);
							break;
						}
					}
				}
				if ((!flag && projectile.HitObjectID == 0) || !UpdateProjectileRunScripts(projectile, objectData, Microsoft.Xna.Framework.Vector2.Zero, ref stopProjectileUpdateProgress))
				{
					if (e.StopProjectileFrameProgress)
					{
						stopProjectileUpdateProgress = true;
						break;
					}
					continue;
				}
				break;
			}
			projectile.CheckTunneling = false;
			if (!projectile.HitFlag)
			{
				if (!stopProjectileUpdateProgress)
				{
					bool flag3 = false;
					for (int i = 0; i < Players.Count; i++)
					{
						flag3 |= Players[i].CheckProjectileDeflection(projectile, position);
					}
					if (!flag3)
					{
						projectile.TotalDistanceTraveled = totalDistanceTraveled;
						projectile.VisualDistanceTraveled = visualDistanceTraveled;
						projectile.PlayerDistanceTraveled = playerDistanceTraveled;
						projectile.Position = position2;
					}
				}
				if (projectile.InWater && !TestInsideWaterZone(Converter.WorldToBox2D(projectile.Position)))
				{
					projectile.OutboundsWaterDistanceTraveled += num;
					if (projectile.OutboundsWaterDistanceTraveled > 8f)
					{
						projectile.InWater = false;
						projectile.OutboundsWaterDistanceTraveled = 0f;
					}
				}
			}
			if (!projectile.HitFlag && ActiveCameraSafeArea.GetDistanceFromEdge(projectile.Position) > 400f && projectile.RemoveWhenOutsideWorld())
			{
				projectile.HitFlag = true;
				projectile.Position = position;
				projectile.HitObjectID = 0;
				projectile.HitFixtureID = "";
				projectile.HitFixtureIndex = -1;
				projectile.OutsideWorld();
			}
			if (GameOwner != GameOwnerEnum.Client && projectile.HitFlag)
			{
				RemovedProjectiles.Add(projectile);
			}
			else if (GameOwner == GameOwnerEnum.Client && projectile.HitFlag && OldProjectiles.Contains(projectile.InstanceID))
			{
				OldProjectiles.Remove(projectile.InstanceID);
				RemoveProjectile(projectile);
			}
		}
		else
		{
			projectile.WaitTime += elapsedSeconds;
			if (projectile.WaitTime > 4f)
			{
				RemoveProjectile(projectile);
			}
		}
	}

	public bool UpdateProjectileRunScripts(Projectile projectile, ObjectData hitObject, Microsoft.Xna.Framework.Vector2 deflectionNormal, ref bool stopProjectileUpdateProgress)
	{
		if (GameOwner != GameOwnerEnum.Client)
		{
			Microsoft.Xna.Framework.Vector2 position = projectile.Position;
			RunScriptOnProjectileHitCallbacks(projectile, hitObject, deflectionNormal);
			if (projectile.RemovalInitiated || projectile.Position != position)
			{
				stopProjectileUpdateProgress = true;
				return true;
			}
		}
		return false;
	}

	public void CheckProjectileFirePowerupEffect(Projectile projectile, ObjectData hitObject)
	{
		if (!projectile.PowerupFireActive || hitObject == null || hitObject.TerminationInitiated)
		{
			return;
		}
		if (GameOwner != GameOwnerEnum.Client)
		{
			hitObject.TakeFireDamage(0f, projectile.Properties.PowerupFireIgniteValue);
			if (projectile.Properties.PowerupFireType == ProjectilePowerupFireType.Default)
			{
				FireGrid.AddFireNodes(Converter.WorldToBox2D(projectile.Position), 0.8f, 0.3f, 2, FireNodeTypeEnum.Default, Microsoft.Xna.Framework.Vector2.Zero);
			}
			else if (projectile.Properties.PowerupFireType == ProjectilePowerupFireType.Fireplosion)
			{
				TriggerFireplosion(projectile.Position, 8f);
				FireGrid.AddFireNodes(Converter.WorldToBox2D(projectile.Position), 2.6f, 0.4f, 18, FireNodeTypeEnum.Default, Microsoft.Xna.Framework.Vector2.Zero);
			}
		}
		if (GameOwner != GameOwnerEnum.Server)
		{
			SoundHandler.PlaySound("Flamethrower", projectile.Position, this);
		}
	}

	public void UpdateAllProjectiles(float ms, bool isFirst)
	{
		if (isFirst)
		{
			try
			{
				if (NewProjectiles.Count > 0)
				{
					int count = NewProjectiles.Count;
					Projectiles.AddRange(NewProjectiles);
					RunScriptOnProjectileCreatedCallbacks(NewProjectiles);
					if (NewProjectiles.Count == count)
					{
						NewProjectiles.Clear();
					}
					else
					{
						NewProjectiles.RemoveRange(0, count);
					}
				}
			}
			catch (Exception ex)
			{
				m_game.ShowError("Error: GameWorld.Projectiles.UpdateAllProjectiles() preparation failed\r\n" + ex.ToString());
				return;
			}
		}
		float elapsedSeconds = ms / 1000f;
		try
		{
			for (int num = Projectiles.Count - 1; num >= 0; num--)
			{
				Projectile projectile = Projectiles[num];
				if (projectile == null)
				{
					Projectiles.RemoveAt(num);
				}
				else
				{
					UpdateProjectile(projectile, elapsedSeconds);
				}
			}
			for (int i = 0; i < Players.Count; i++)
			{
				Player player = Players[i];
				if (player != null)
				{
					player.DeflectBulletFrameWindowTime -= ms;
					player.DeflectBulletFirstAttackFrameWindow = false;
				}
			}
		}
		catch (Exception ex2)
		{
			m_game.ShowError("Error: GameWorld.Projectiles.UpdateAllProjectiles() main update failed\r\n" + ex2.ToString());
			return;
		}
		if (GameOwner == GameOwnerEnum.Client)
		{
			return;
		}
		try
		{
			foreach (Projectile removedProjectile in RemovedProjectiles)
			{
				Projectiles.Remove(removedProjectile);
				removedProjectile.Dispose();
			}
		}
		catch (Exception ex3)
		{
			m_game.ShowError("Error: GameWorld.Projectiles.UpdateAllProjectiles() projectile removal failed\r\n" + ex3.ToString());
		}
	}

	public void CheckProjectileTunneling(Projectile projectile)
	{
		List<KeyValuePair<RayCastOutput, Fixture>> list = null;
		Box2D.XNA.RayCastInput rayCastInput = default(Box2D.XNA.RayCastInput);
		if (projectile.CheckPlayerTunneling)
		{
			rayCastInput.maxFraction = 1f;
			rayCastInput.p1 = Converter.WorldToBox2D(projectile.PlayerOriginalPosition);
			rayCastInput.p2 = Converter.WorldToBox2D(projectile.TunnelingPosition);
			list = CheckProjectileHit(projectile, ref rayCastInput, environmentCollision: true, playerCollision: false, includeOverlappingFixture: false, TunnelingCheckType.FeetToProjectileBase);
			if (list != null && list.Count > 0)
			{
				bool flag = true;
				for (int i = 0; i < list.Count; i++)
				{
					if (!(Microsoft.Xna.Framework.Vector2.Dot(projectile.Direction, list[i].Key.normal) < 0.2f) && !(list[i].Key.fraction < 0.5f))
					{
						continue;
					}
					ObjectData objectData = ObjectData.Read(list[i].Value);
					if ((!(list[i].Value.ObjectStrength > 0f) || !(projectile.StrengthLeft > list[i].Value.ObjectStrength) || objectData == null || !objectData.Destructable) && list[i].Value.AbsorbProjectile)
					{
						flag = false;
						projectile.Direction = -list[i].Key.normal;
						if (i > 0)
						{
							list.RemoveRange(0, i);
						}
						break;
					}
				}
				if (flag)
				{
					list = null;
				}
			}
		}
		if (list == null || list.Count == 0)
		{
			rayCastInput.maxFraction = 1f;
			rayCastInput.p1 = Converter.WorldToBox2D(projectile.TunnelingPosition);
			rayCastInput.p2 = Converter.WorldToBox2D(projectile.Position);
			list = CheckProjectileHit(projectile, ref rayCastInput, environmentCollision: true, playerCollision: false, includeOverlappingFixture: false, TunnelingCheckType.ProjectileBaseToSpawnPosition);
		}
		if (list != null && list.Count > 0)
		{
			float num = list[0].Key.fraction - 0.0001f;
			Microsoft.Xna.Framework.Vector2 box2DValue = (1f - num) * rayCastInput.p1 + num * rayCastInput.p2;
			projectile.Position = Converter.Box2DToWorld(box2DValue);
		}
		else
		{
			if (projectile.PlayerOwnerID == 0)
			{
				return;
			}
			Player playerOwner = projectile.PlayerOwner;
			if (playerOwner == null || playerOwner.IsRemoved)
			{
				return;
			}
			float num2 = 1f;
			foreach (Player player in Players)
			{
				if (player.IsRemoved || projectile.PlayerOwnerID == player.ObjectID)
				{
					continue;
				}
				player.GetAABBWhole(out var aabb);
				if (!aabb.Contains(ref rayCastInput.p2))
				{
					RayCastOutput output = default(RayCastOutput);
					if (aabb.RayCast(out output, ref rayCastInput, startInsideCollision: true) && !playerOwner.InSameTeam(player) && player.TestProjectileHit(projectile))
					{
						num2 = 0.5f;
					}
				}
			}
			Microsoft.Xna.Framework.Vector2 box2DValue2 = (1f - num2) * rayCastInput.p1 + num2 * rayCastInput.p2;
			projectile.Position = Converter.Box2DToWorld(box2DValue2);
		}
	}

	public List<KeyValuePair<RayCastOutput, Fixture>> CheckProjectileHit(Projectile projectile, ref Box2D.XNA.RayCastInput rayCastInput, bool environmentCollision, bool playerCollision, bool includeOverlappingFixture, TunnelingCheckType tunnelingCheck)
	{
		List<KeyValuePair<RayCastOutput, Fixture>> hits = new List<KeyValuePair<RayCastOutput, Fixture>>(10);
		Box2D.XNA.RayCastInput _rci = rayCastInput;
		RayCastOutput _rco = default(RayCastOutput);
		AABB.Create(out var aabb, _rci.p1, _rci.p2, 0.005f);
		if (environmentCollision)
		{
			b2_world_active.QueryAABB(delegate(Fixture fixture)
			{
				if (fixture != null && fixture.GetUserData() != null)
				{
					ObjectData objectData = ObjectData.Read(fixture);
					if (projectile.CheckProjectileHit(objectData, fixture) && (tunnelingCheck == TunnelingCheckType.None || (tunnelingCheck == TunnelingCheckType.FeetToProjectileBase && objectData.ProjectileTunnelingCheck == ProjectileTunnelingCheck.Full) || (tunnelingCheck == TunnelingCheckType.ProjectileBaseToSpawnPosition && (objectData.ProjectileTunnelingCheck == ProjectileTunnelingCheck.Full || objectData.ProjectileTunnelingCheck == ProjectileTunnelingCheck.IgnoreFeetPerformArm))) && !objectData.IsPlayer && projectile.ObjectIDToIgnore != objectData.ObjectID)
					{
						if (fixture.RayCast(out _rco, ref _rci))
						{
							bool flag = true;
							foreach (KeyValuePair<RayCastOutput, Fixture> item in hits)
							{
								if (item.Value == fixture)
								{
									flag = false;
									break;
								}
							}
							if (flag)
							{
								hits.Add(new KeyValuePair<RayCastOutput, Fixture>(_rco, fixture));
							}
						}
						else if (includeOverlappingFixture && fixture.TestPoint(_rci.p1))
						{
							bool flag2 = true;
							foreach (KeyValuePair<RayCastOutput, Fixture> item2 in hits)
							{
								if (item2.Value == fixture)
								{
									flag2 = false;
									break;
								}
							}
							if (flag2)
							{
								Box2D.XNA.RayCastInput input = _rci;
								input.p1 -= projectile.Direction * 0.04f * 8f;
								if (!fixture.RayCast(out var output, ref input))
								{
									output.normal = -projectile.Direction;
								}
								output.fraction = 0f;
								hits.Add(new KeyValuePair<RayCastOutput, Fixture>(output, fixture));
							}
						}
					}
				}
				return true;
			}, ref aabb);
		}
		if (playerCollision)
		{
			foreach (Player player in Players)
			{
				if (!player.IsRemoved && (projectile.PlayerDistanceTraveled > 24f || projectile.PlayerOwnerID != player.ObjectID))
				{
					player.GetAABBWhole(out var aabb2);
					Microsoft.Xna.Framework.Vector2 vector = player.CalcServerPositionDifference();
					aabb2.lowerBound -= vector;
					aabb2.upperBound -= vector;
					if (AABB.TestOverlap(ref aabb2, ref aabb) && aabb2.RayCast(out _rco, ref _rci, includeOverlappingFixture) && player.TestProjectileHit(projectile))
					{
						hits.Add(new KeyValuePair<RayCastOutput, Fixture>(_rco, player.WorldBody.GetFixtureList()));
					}
				}
			}
		}
		hits.Sort((KeyValuePair<RayCastOutput, Fixture> p1, KeyValuePair<RayCastOutput, Fixture> p2) => p1.Key.fraction.CompareTo(p2.Key.fraction));
		return hits;
	}

	public RayCastResult RayCast(Microsoft.Xna.Framework.Vector2 startPosition, Microsoft.Xna.Framework.Vector2 direction, float tunnelingDistance, float maxDistance, RayCastFixtureCheck fixtureCheck, RayCastPlayerCheck playerCheck)
	{
		RayCastResult result = default(RayCastResult);
		result.StartPosition = startPosition;
		result.EndPosition = startPosition + direction * maxDistance;
		result.TotalDistance = maxDistance;
		result.Direction = direction;
		result.Fraction = 1f;
		result.TunnelCollision = false;
		result.EndFixture = null;
		Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertWorldToBox2D(startPosition);
		Microsoft.Xna.Framework.Vector2 vector2 = vector + direction * Converter.ConvertWorldToBox2D(maxDistance);
		Microsoft.Xna.Framework.Vector2 vector3 = vector - direction * Converter.ConvertWorldToBox2D(tunnelingDistance);
		float minFraction = 1f;
		HashSet<Player> checkedPlayers = new HashSet<Player>();
		bool tunnelCollision = false;
		AABB.Create(out var aabb, vector, vector3, 0.01f);
		Box2D.XNA.RayCastInput rciFixtureCheck = default(Box2D.XNA.RayCastInput);
		Box2D.XNA.RayCastInput rci = default(Box2D.XNA.RayCastInput);
		RayCastOutput rco = default(RayCastOutput);
		rci.p1 = vector3;
		rci.p2 = vector;
		rci.maxFraction = 1f;
		rciFixtureCheck.p1 = vector;
		rciFixtureCheck.p2 = vector2;
		rciFixtureCheck.maxFraction = 1f;
		GetActiveWorld.QueryAABB(delegate(Fixture fixture)
		{
			if (fixture != null && fixture.GetUserData() != null && !fixture.IsSensor())
			{
				ObjectData objectData = ObjectData.Read(fixture);
				if (objectData.IsPlayer)
				{
					Player player2 = (Player)objectData.InternalData;
					if (!checkedPlayers.Contains(player2))
					{
						player2.GetAABBWhole(out var aabb3);
						if ((aabb3.Contains(ref rci.p2) || aabb3.RayCast(out rco, ref rci)) && playerCheck(player2))
						{
							checkedPlayers.Add(player2);
							tunnelCollision = true;
							result.EndFixture = fixture;
							return false;
						}
					}
				}
				else if (fixtureCheck(fixture))
				{
					if (fixture.TestPoint(rci.p2))
					{
						tunnelCollision = true;
						result.EndFixture = fixture;
						return false;
					}
					if (fixture.RayCast(out rco, ref rci))
					{
						tunnelCollision = true;
						result.EndFixture = fixture;
						return false;
					}
				}
			}
			return true;
		}, ref aabb);
		if (tunnelCollision)
		{
			result.TunnelCollision = true;
			result.TotalDistance = 0f;
			result.EndPosition = result.StartPosition;
			result.Fraction = 0f;
			return result;
		}
		GetActiveWorld.RayCast(delegate(Fixture fixture, Microsoft.Xna.Framework.Vector2 point, Microsoft.Xna.Framework.Vector2 normal, float fraction)
		{
			if (fixture != null && fixture.GetUserData() != null && !fixture.IsSensor())
			{
				ObjectData objectData = ObjectData.Read(fixture);
				if (objectData.IsPlayer)
				{
					Player player2 = (Player)objectData.InternalData;
					if (checkedPlayers.Add(player2))
					{
						player2.GetAABBWhole(out var aabb3);
						if (aabb3.RayCast(out rco, ref rciFixtureCheck) && playerCheck(player2))
						{
							if (minFraction > rco.fraction)
							{
								result.EndFixture = fixture;
								minFraction = rco.fraction;
							}
							return rco.fraction;
						}
					}
					return -1f;
				}
				if (fixtureCheck(fixture))
				{
					if (minFraction > fraction)
					{
						result.EndFixture = fixture;
						minFraction = fraction;
					}
					return fraction;
				}
				return 1f;
			}
			return -1f;
		}, vector, vector2);
		for (int num = 0; num < Players.Count; num++)
		{
			Player player = Players[num];
			if (checkedPlayers.Add(player))
			{
				player.GetAABBWhole(out var aabb2);
				if (aabb2.RayCast(out rco, ref rciFixtureCheck) && playerCheck(player) && minFraction > rco.fraction)
				{
					result.EndFixture = player.GetFixturePolygon();
					minFraction = rco.fraction;
				}
			}
		}
		result.Fraction = minFraction;
		result.EndPosition = startPosition + direction * maxDistance * minFraction;
		return result;
	}

	public static Type[] GetScriptTypes()
	{
		return new Type[15]
		{
			typeof(object),
			typeof(GameScriptInterface),
			typeof(GameWorld),
			typeof(Enumerable),
			typeof(EnumerableQuery),
			typeof(IQueryable),
			typeof(ArrayList),
			typeof(List<>),
			typeof(Dictionary<, >),
			typeof(SortedList),
			typeof(OrderedDictionary),
			typeof(SortedList<, >),
			typeof(ConcurrentDictionary<, >),
			typeof(StringBuilder),
			typeof(Regex)
		};
	}

	public static List<FullScriptSanitizeError> GetFullScript(string innerScript, out string fullScript, bool fancyFormatting = false)
	{
		List<FullScriptSanitizeError> result = new List<FullScriptSanitizeError>();
		if (fancyFormatting && innerScript.Length > 0)
		{
			innerScript = $"/* ---- Map script (generated {DateTime.Now.ToString()}) ---- */\n" + innerScript + "\n ";
			int num = 0;
			while (num != -1)
			{
				innerScript = innerScript.Insert(num, "        ");
				num = innerScript.IndexOf("\n", num);
				if (num >= 0)
				{
					num++;
				}
			}
		}
		fullScript = "using System;\r\nusing System.Linq;\r\nusing System.Collections;\r\nusing System.Collections.Generic;\r\nusing System.Text;\r\nusing System.Text.RegularExpressions;\r\nusing SFDGameScriptInterface;\r\n\r\nnamespace SFDScript\r\n{\r\n    public static class SFD\r\n    {\r\n        public static IGame Game { get { return GameScript.Game; } }\r\n    }\r\n    \r\n    public class GameScript : GameScriptInterface\r\n    {\r\n        // Cancellation token used for cooperative cancellation of scripts, set by the sandbox environment.\r\n        public static System.Threading.CancellationToken __sandboxCancellationToken;\r\n\r\n        // Needs to be static for script compatability reasons.\r\n        // Static needs to live in this GameScript class to be isolated to compiled assemblies.\r\n        private static IGame __game = null;\r\n        public static IGame Game { get { return __game; } }\r\n\r\n        protected override void __onDispose() { __game = null; }\r\n\r\n        // SFDScript.GameScript\r\n        public GameScript(IGame game) : base() { __game = game; }\r\n" + innerScript + "\r\n    }\r\n}";
		return result;
	}

	public static string ConvertLegacyCalls(string fullScript)
	{
		if (!string.IsNullOrEmpty(fullScript))
		{
			string[] array = new string[17]
			{
				"ExplosionHitCallback", "ObjectCreatedCallback", "ObjectDamageCallback", "ObjectTerminatedCallback", "PlayerCreatedCallback", "PlayerDamageCallback", "PlayerDeathCallback", "PlayerKeyInputCallback", "PlayerMeleeActionCallback", "PlayerWeaponAddedActionCallback",
				"PlayerWeaponRemovedActionCallback", "ProjectileCreatedCallback", "ProjectileHitCallback", "UpdateCallback", "UserJoinCallback", "UserLeaveCallback", "UserMessageCallback"
			};
			foreach (string text in array)
			{
				fullScript = fullScript.Replace("Events." + text + ".Start(", "Game.Events.Start" + text + "(");
				fullScript = fullScript.Replace("Events." + text + ".Stop(", "Game.Events.Stop(");
			}
		}
		return fullScript;
	}

	public static RosScriptCompilerResult CompileScript(string fullScript, string outputAssembly, bool generateDebugInformation)
	{
		fullScript = ConvertLegacyCalls(fullScript);
		int length = "using System;\r\nusing System.Linq;\r\nusing System.Collections;\r\nusing System.Collections.Generic;\r\nusing System.Text;\r\nusing System.Text.RegularExpressions;\r\nusing SFDGameScriptInterface;\r\n\r\nnamespace SFDScript\r\n{\r\n    public static class SFD\r\n    {\r\n        public static IGame Game { get { return GameScript.Game; } }\r\n    }\r\n    \r\n    public class GameScript : GameScriptInterface\r\n    {\r\n        // Cancellation token used for cooperative cancellation of scripts, set by the sandbox environment.\r\n        public static System.Threading.CancellationToken __sandboxCancellationToken;\r\n\r\n        // Needs to be static for script compatability reasons.\r\n        // Static needs to live in this GameScript class to be isolated to compiled assemblies.\r\n        private static IGame __game = null;\r\n        public static IGame Game { get { return __game; } }\r\n\r\n        protected override void __onDispose() { __game = null; }\r\n\r\n        // SFDScript.GameScript\r\n        public GameScript(IGame game) : base() { __game = game; }\r\n".Length;
		return RosScriptCompiler.CompileScript(fullScript, length, outputAssembly, RosScriptCompiler.GetAssemblies(GetScriptTypes()), generateDebugInformation);
	}

	public void StartDefaultScript()
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: GameWorld.StartDefaultScript() is SERVER/LOCAL ONLY");
		}
		DefaultScript = new SandboxInstance("Map", "", "");
		StartScript(DefaultScript, GetInnerScript());
	}

	public bool StartScript(SandboxInstance sandboxInstance, string script)
	{
		if (GameOwner == GameOwnerEnum.Client)
		{
			throw new Exception("Error: GameWorld.StartScript() is SERVER/LOCAL ONLY");
		}
		ConsoleOutput.ShowMessage(ConsoleOutputType.Information, $"GameWorld.StartScript (Constants.ENABLE_MAP_SCRIPTS={Constants.ENABLE_MAP_SCRIPTS})");
		if (!Constants.ENABLE_MAP_SCRIPTS)
		{
			return false;
		}
		if (ScriptExist(script))
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Starting compilation of script...");
			RosScriptCompilerResult rosScriptCompilerResult = null;
			string outputAssembly = "";
			sandboxInstance.SandboxAssemblyLocation = null;
			sandboxInstance.DebugFiles = null;
			try
			{
				bool flag;
				if (flag = GameSFD.Handle.CurrentState == State.EditorTestRun && !string.IsNullOrEmpty(Constants.DEVENV) && Debugger.IsAttached)
				{
					try
					{
						outputAssembly = Constants.GetScriptTempPath();
					}
					catch (Exception exception)
					{
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Unable to create temp-file");
						MessageStack.Show(LanguageHelper.GetText("error.mapScript.createTempFileError"), MessageStackType.Error);
						Program.LogErrorMessage("Script_CreateTempFileError", exception);
						return false;
					}
				}
				string fullScript = "";
				List<FullScriptSanitizeError> fullScript2 = GetFullScript(script, out fullScript, flag);
				if (fullScript2.Count > 0)
				{
					if (Program.IsGame && m_game.CurrentState == State.EditorTestRun)
					{
						string text = "";
						for (int i = 0; i < fullScript2.Count; i++)
						{
							text = ((!string.IsNullOrEmpty(text)) ? $"{text}\r\n{fullScript2[i].Message}" : fullScript2[i].Message);
						}
						ShowScriptCompileError(text);
					}
					return false;
				}
				rosScriptCompilerResult = CompileScript(fullScript, outputAssembly, flag);
			}
			catch (Exception exception2)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Compile exception");
				MessageStack.Show(string.Format("File or Security exception when compiling map script. See error report for more details.", Array.Empty<object>()), MessageStackType.Error);
				Program.LogErrorMessage("Script_CompileException", "File or Security exception when compiling map script. Try to run the game as administrator.", exception2);
				return false;
			}
			sandboxInstance.SandboxAssemblyLocation = rosScriptCompilerResult.AssemblyLocation;
			sandboxInstance.DebugFiles = rosScriptCompilerResult.DebugFiles;
			string text2 = null;
			if (rosScriptCompilerResult.DebugFiles != null && rosScriptCompilerResult.DebugFiles.Length != 0)
			{
				string[] debugFiles = rosScriptCompilerResult.DebugFiles;
				foreach (string text3 in debugFiles)
				{
					ConsoleOutput.ShowMessage(ConsoleOutputType.ScriptFiles, $"Script file '{text3}' created");
					if (text3.EndsWith(".cs") && !string.IsNullOrEmpty(Constants.DEVENV) && Debugger.IsAttached)
					{
						text2 = text3;
					}
				}
			}
			if (rosScriptCompilerResult.HasErrors)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Script " + sandboxInstance.ScriptName + " contains errors. Ignoring script...");
				MessageStack.Show(LanguageHelper.GetText("error.mapScript.scriptError", sandboxInstance.ScriptName), MessageStackType.Error);
				if (Program.IsGame && m_game.CurrentState == State.EditorTestRun)
				{
					string error = string.Join("\r\n", rosScriptCompilerResult.Errors.Select((ErrorInfo x) => x.ErrorText));
					ShowScriptCompileError(error);
				}
				return false;
			}
			try
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Creating sandbox...");
				sandboxInstance.Sandbox = Sandbox.Create(sandboxInstance.UniqueScriptInstanceID);
				sandboxInstance.Sandbox.DebugSourceFile = text2;
				sandboxInstance.SandboxCancellationTokenSource = new CancellationTokenSource();
				ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Sandbox created");
			}
			catch (SecurityException exception3)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Failed to create sandbox - SecurityException. Ignoring script...");
				MessageStack.Show("Could not create a sandbox to be used for scripts. Scripts will be disabled.", MessageStackType.Error);
				MessageStack.Show("Security exception prevents the sanbox from being created.", MessageStackType.Error);
				Program.LogErrorMessage("Script_CreateSandboxFailedSecurityException", exception3);
				return false;
			}
			catch (Exception exception4)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Failed to create sandbox. Ignoring script...");
				MessageStack.Show("Could not create a sandbox to be used for scripts. Scripts will be disabled.", MessageStackType.Error);
				Program.LogErrorMessage("Script_CreateSandboxFailed", exception4);
				return false;
			}
			try
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Loading script assembly into sandbox...");
				if (!string.IsNullOrEmpty(rosScriptCompilerResult.AssemblyLocation))
				{
					sandboxInstance.Sandbox.Load(rosScriptCompilerResult.AssemblyLocation);
				}
				else
				{
					sandboxInstance.Sandbox.Load(rosScriptCompilerResult.AssemblyBytes);
				}
			}
			catch (Exception exception5)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Loading script assembly failed");
				MessageStack.Show("Could not load assembly into the sandbox to be used for scripts. Scripts will be disabled.", MessageStackType.Error);
				Program.LogErrorMessage("Script_LoadSandboxAssemblyFailed", exception5);
				return false;
			}
			if (!string.IsNullOrEmpty(text2))
			{
				try
				{
					using Process process = new Process();
					ProcessStartInfo startInfo = new ProcessStartInfo(Constants.DEVENV, $"/edit \"{text2}\"");
					process.StartInfo = startInfo;
					process.Start();
					Thread.Sleep(10);
				}
				catch (Exception exception6)
				{
					Program.LogErrorMessage("Script_DevEnvStartFailed", exception6);
				}
			}
			IGame scriptBridge = ScriptBridge;
			try
			{
				sandboxInstance.Sandbox.CreateInstance(WorldID, "SFDScript.GameScript", scriptBridge);
				sandboxInstance.Sandbox.SetField(WorldID, "__sandboxCancellationToken", sandboxInstance.SandboxCancellationTokenSource.Token);
			}
			catch (Exception ex)
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Creating script instance failed");
				Program.LogErrorMessage("Script_CreateScriptInstanceFailed", ex);
				if (ex.InnerException != null && ex.InnerException.StackTrace.Contains("SFDScript.GameScript..ctor(IGame game)"))
				{
					if (ex.InnerException is NullReferenceException)
					{
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Constructor failed with null reference exception");
						MessageStack.Show("Creating script instance failed with null reference exception. Scripts will be disabled.", MessageStackType.Error);
						MessageStack.Show(LanguageHelper.GetText("error.mapScript.propertyGameNotice"), MessageStackType.Error);
					}
					else
					{
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Constructor failed with unhandled error");
						MessageStack.Show("Creating script instance failed with unknown error. Scripts will be disabled.", MessageStackType.Error);
					}
					return false;
				}
				MessageStack.Show("Creating script instance failed. Scripts will be disabled.", MessageStackType.Error);
				return false;
			}
			return true;
		}
		return false;
	}

	public void StopScript(SandboxInstance sandboxInstance, bool runOnShotdown = true)
	{
		if (sandboxInstance == null)
		{
			return;
		}
		if (!m_scriptsRunOnShutdown.Remove(sandboxInstance.UniqueScriptInstanceID) && runOnShotdown)
		{
			try
			{
				CallScriptInstance(sandboxInstance.UniqueScriptInstanceID, "OnShutdown", showError: false, null);
			}
			catch (Exception)
			{
			}
		}
		AllowDisposeOfScripts = true;
		DisposeAllCallbacksForScript(sandboxInstance.UniqueScriptInstanceID);
		Sandbox sandbox = sandboxInstance.Sandbox;
		sandboxInstance.Sandbox = null;
		try
		{
			if (sandbox != null && sandbox.ContainsInstance(WorldID))
			{
				if (sandboxInstance.SandboxCancellationTokenSource == null || sandboxInstance.SandboxCancellationTokenSource.IsCancellationRequested)
				{
					sandboxInstance.SandboxCancellationTokenSource?.Dispose();
					sandboxInstance.SandboxCancellationTokenSource = new CancellationTokenSource();
				}
				sandbox.ExecuteInstance(WorldID, "Dispose", sandboxInstance.SandboxCancellationTokenSource);
				sandbox.RemoveInstance(WorldID);
			}
		}
		catch (Exception)
		{
		}
		sandboxInstance.SandboxCancellationTokenSource?.Cancel();
		sandboxInstance.SandboxCancellationTokenSource?.Dispose();
		sandboxInstance.SandboxCancellationTokenSource = null;
		try
		{
			if (sandbox != null)
			{
				if (!Sandbox.Unload(sandbox))
				{
					ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Failed to unload assemblies from sandbox '" + sandboxInstance.ScriptName + "'");
				}
				else
				{
					ConsoleOutput.ShowMessage(ConsoleOutputType.Information, "Successfully unloaded assemblies from sandbox '" + sandboxInstance.ScriptName + "'");
				}
			}
		}
		catch (Exception ex3)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, "Failed to unload sandbox. " + ex3.Message);
		}
		try
		{
			sandboxInstance.DeleteAssembly();
		}
		catch (Exception ex4)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Failed to delete script assembly file {sandboxInstance.SandboxAssemblyLocation}: {ex4.Message}");
		}
		sandboxInstance.SandboxAssemblyLocation = null;
		sandboxInstance.DebugFiles = null;
		if (ExtensionScripts.ContainsKey(sandboxInstance.ScriptInstanceID))
		{
			ExtensionScripts.Remove(sandboxInstance.ScriptInstanceID);
			ExtensionScriptsUniqueID.Remove(sandboxInstance.UniqueScriptInstanceID);
		}
		AllowDisposeOfScripts = false;
	}

	public void SetInnerScript(string script)
	{
		m_script = script;
	}

	public string GetInnerScript()
	{
		return m_script;
	}

	public int GetHeaderScriptRowCount()
	{
		return "using System;\r\nusing System.Linq;\r\nusing System.Collections;\r\nusing System.Collections.Generic;\r\nusing System.Text;\r\nusing System.Text.RegularExpressions;\r\nusing SFDGameScriptInterface;\r\n\r\nnamespace SFDScript\r\n{\r\n    public static class SFD\r\n    {\r\n        public static IGame Game { get { return GameScript.Game; } }\r\n    }\r\n    \r\n    public class GameScript : GameScriptInterface\r\n    {\r\n        // Cancellation token used for cooperative cancellation of scripts, set by the sandbox environment.\r\n        public static System.Threading.CancellationToken __sandboxCancellationToken;\r\n\r\n        // Needs to be static for script compatability reasons.\r\n        // Static needs to live in this GameScript class to be isolated to compiled assemblies.\r\n        private static IGame __game = null;\r\n        public static IGame Game { get { return __game; } }\r\n\r\n        protected override void __onDispose() { __game = null; }\r\n\r\n        // SFDScript.GameScript\r\n        public GameScript(IGame game) : base() { __game = game; }\r\n".Split(new char[1] { '\n' }).Length - 1;
	}

	public bool ScriptExist(string script)
	{
		string[] array = script.Split(new char[1] { '\n' });
		int num = 0;
		while (true)
		{
			if (num < array.Length)
			{
				string text = array[num].Replace(" ", "");
				if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("//"))
				{
					break;
				}
				num++;
				continue;
			}
			return false;
		}
		return true;
	}

	public void DisposeAllCallbacksForScript(string scriptInstanceID)
	{
		if (m_scriptRegisteredUpdateCallbacks == null)
		{
			return;
		}
		List<ScriptCallbackEvent> list = new List<ScriptCallbackEvent>();
		for (int i = 0; i < m_scriptRegisteredUpdateCallbacks.Count; i++)
		{
			ScriptCallbackEvent scriptCallbackEvent = m_scriptRegisteredUpdateCallbacks[i];
			if (scriptCallbackEvent.ScriptInstanceUniqueID == scriptInstanceID)
			{
				list.Add(scriptCallbackEvent);
			}
		}
		if (list == null || list.Count <= 0)
		{
			return;
		}
		foreach (ScriptCallbackEvent item in list)
		{
			item.Func?.Dispose();
			item.Func = null;
			item.Active = false;
			m_scriptRegisteredUpdateCallbacks.Remove(item);
		}
	}

	public void DisposeAllCallbacksForAllScripts()
	{
		if (m_scriptRegisteredUpdateCallbacks != null)
		{
			List<ScriptCallbackEvent> scriptRegisteredUpdateCallbacks = m_scriptRegisteredUpdateCallbacks;
			m_scriptRegisteredUpdateCallbacks = null;
			foreach (ScriptCallbackEvent item in scriptRegisteredUpdateCallbacks)
			{
				item.Func?.Dispose();
				item.Func = null;
				item.Active = false;
			}
			scriptRegisteredUpdateCallbacks.Clear();
			scriptRegisteredUpdateCallbacks = null;
		}
		m_scriptRegisteredUpdateCallbacksArray = null;
		m_scriptRegisteredUpdateCallbacksDirty = true;
	}

	public bool AddScriptCallback(Events.CallbackDelegate func)
	{
		if (func == null)
		{
			return false;
		}
		if (m_scriptRegisteredUpdateCallbacks == null)
		{
			m_scriptRegisteredUpdateCallbacks = new List<ScriptCallbackEvent>();
		}
		ScriptCallbackEvent scriptCallbackEvent = m_scriptRegisteredUpdateCallbacks.Find((ScriptCallbackEvent e) => e.Func == func);
		if (scriptCallbackEvent == null)
		{
			scriptCallbackEvent = CreateScriptCallbackWrapper(func);
			m_scriptRegisteredUpdateCallbacks.Add(scriptCallbackEvent);
			m_scriptRegisteredUpdateCallbacksDirty = true;
			return true;
		}
		return false;
	}

	public bool RemoveScriptCallback(Events.CallbackDelegate func)
	{
		if (func != null && m_scriptRegisteredUpdateCallbacks != null)
		{
			ScriptCallbackEvent scriptCallbackEvent = m_scriptRegisteredUpdateCallbacks.Find((ScriptCallbackEvent e) => e.ScriptInstanceUniqueID == CurrentActiveScriptInstanceUniqueID && e.Func == func);
			if (scriptCallbackEvent != null)
			{
				scriptCallbackEvent.Func = null;
				scriptCallbackEvent.Active = false;
				m_scriptRegisteredUpdateCallbacks.Remove(scriptCallbackEvent);
				m_scriptRegisteredUpdateCallbacksDirty = true;
				return true;
			}
			return false;
		}
		return false;
	}

	public void RunScriptCallbacks<T>(Func<T, bool> handleCallback) where T : ScriptCallbackEvent
	{
		if (m_scriptRegisteredUpdateCallbacks == null || m_scriptRegisteredUpdateCallbacks.Count <= 0)
		{
			return;
		}
		if (m_scriptRegisteredUpdateCallbacksArray == null || m_scriptRegisteredUpdateCallbacksArray.Length != m_scriptRegisteredUpdateCallbacks.Count)
		{
			m_scriptRegisteredUpdateCallbacksArray = new ScriptCallbackEvent[m_scriptRegisteredUpdateCallbacks.Count];
			m_scriptRegisteredUpdateCallbacksDirty = true;
		}
		if (m_scriptRegisteredUpdateCallbacksDirty)
		{
			m_scriptRegisteredUpdateCallbacks.CopyTo(m_scriptRegisteredUpdateCallbacksArray);
			m_scriptRegisteredUpdateCallbacksDirty = false;
		}
		for (int i = 0; i < m_scriptRegisteredUpdateCallbacksArray.Length; i++)
		{
			ScriptCallbackEvent callback = m_scriptRegisteredUpdateCallbacksArray[i];
			if (!callback.Active || callback.Func == null || !(callback is T))
			{
				continue;
			}
			SandboxInstance sandboxInstance = ScriptPushCallingScript(callback.ScriptInstanceUniqueID);
			if (sandboxInstance == null)
			{
				continue;
			}
			try
			{
				ScriptExecutor.ExecuteWithTimeout(sandboxInstance.Sandbox.Debugging, typeof(T).ToString(), delegate
				{
					if (handleCallback((T)callback))
					{
						callback.LastElapsedTotalTime = ElapsedTotalRealTime;
					}
				}, sandboxInstance.SandboxCancellationTokenSource, 4);
			}
			catch (SecurityException e)
			{
				RemoveScriptCallback(callback.Func);
				if (m_failedScriptMethods == null)
				{
					m_failedScriptMethods = new HashSet<string>();
				}
				ScriptEventException ex = new ScriptEventException(e);
				ex.SetMethodFromTypeName(callback.ToString());
				if (!m_failedScriptMethods.Contains(ex.method))
				{
					m_failedScriptMethods.Add(ex.method);
					if (Program.IsGame)
					{
						ShowScriptEventException(ex);
					}
					ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Script: Method '{ex.method}' security exception in script '{sandboxInstance.ScriptInstanceID}'");
					MessageStack.Show(LanguageHelper.GetText("error.mapScript.scriptMethodSecurityError", ex.method, sandboxInstance.ScriptInstanceID), MessageStackType.Error);
					MessageStack.Show(ex.method, MessageStackType.Error);
				}
			}
			catch (Exception ex2)
			{
				RemoveScriptCallback(callback.Func);
				if (m_failedScriptMethods == null)
				{
					m_failedScriptMethods = new HashSet<string>();
				}
				ScriptEventException ex3 = new ScriptEventException(ex2);
				ex3.SetMethodFromTypeName(callback.ToString());
				if (!m_failedScriptMethods.Contains(ex3.method))
				{
					m_failedScriptMethods.Add(ex3.method);
					if (Program.IsGame)
					{
						ShowScriptEventException(ex3);
					}
					ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Script: {ex3.method} Method '{sandboxInstance.ScriptInstanceID}' error");
					MessageStack.Show(LanguageHelper.GetText("error.mapScript.scriptMethodError", ex3.method, sandboxInstance.ScriptName), MessageStackType.Error);
					MessageStack.Show(ex3.method, MessageStackType.Error);
				}
				if (ex2 is SandboxExecutionTimeoutException)
				{
					bool num = !IsCorrupted;
					IsCorrupted = true;
					ScriptPopCallingScript();
					if (num)
					{
						throw new GameWorldCorruptedException();
					}
					break;
				}
			}
			ScriptPopCallingScript();
		}
	}

	public SandboxInstance ScriptPushCallingScript(string scriptInstanceUniqueID)
	{
		SandboxInstance callingScriptFromUniqueInstanceID = GetCallingScriptFromUniqueInstanceID(scriptInstanceUniqueID);
		if (callingScriptFromUniqueInstanceID != null)
		{
			CallingScriptInstance.Push(callingScriptFromUniqueInstanceID);
			return callingScriptFromUniqueInstanceID;
		}
		return null;
	}

	public void ScriptPopCallingScript()
	{
		CallingScriptInstance.Pop();
	}

	public void RunScriptOnPlayerCreatedCallbacks(List<Player> players)
	{
		if (ScriptCallbackExists_PlayerCreated && players != null)
		{
			IPlayer[] scriptPlayers = new IPlayer[players.Count];
			for (int i = 0; i < scriptPlayers.Length; i++)
			{
				scriptPlayers[i] = players[i].ScriptBridge;
			}
			RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerCreated callback)
			{
				callback.Func.Invoke(scriptPlayers);
				return true;
			});
		}
	}

	public void RunScriptOnUserJoinCallbacks(GameUser[] users)
	{
		if (!ScriptCallbackExists_UserJoin || users == null)
		{
			return;
		}
		List<IUser> scriptUsers = new List<IUser>();
		foreach (GameUser gameUser in users)
		{
			UserScriptBridge userScriptBridge = GetUserScriptBridge(gameUser.UserIdentifier);
			if (userScriptBridge != null)
			{
				scriptUsers.Add(userScriptBridge);
			}
		}
		RunScriptCallbacks(delegate(ScriptCallbackEvent_UserJoin callback)
		{
			callback.Func.Invoke(scriptUsers.ToArray());
			return true;
		});
	}

	public void RunScriptOnUserLeaveCallbacks(GameUser[] users, int disconnectionType)
	{
		if (!ScriptCallbackExists_UserLeave || users == null)
		{
			return;
		}
		List<IUser> scriptUsers = new List<IUser>();
		foreach (GameUser gameUser in users)
		{
			UserScriptBridge userScriptBridge = GetUserScriptBridge(gameUser.UserIdentifier);
			if (userScriptBridge != null)
			{
				scriptUsers.Add(userScriptBridge);
			}
		}
		RunScriptCallbacks(delegate(ScriptCallbackEvent_UserLeave callback)
		{
			callback.Func.Invoke(scriptUsers.ToArray(), (DisconnectionType)disconnectionType);
			return true;
		});
	}

	public void RunScriptOnPlayerDamageCallbacks(Player player, PlayerDamageEventType damageType, float damage, bool overkillDamage, int sourceID)
	{
		if (!ScriptCallbackExists_PlayerDamage || player == null)
		{
			return;
		}
		PlayerDamageArgs args = new PlayerDamageArgs(damageType, damage, overkillDamage, sourceID);
		RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerDamage callback)
		{
			if (player.ScriptBridge != null)
			{
				callback.Func.Invoke(player.ScriptBridge, args);
			}
			return true;
		});
	}

	public void RunScriptOnPlayerDeathCallbacks(Player player, PlayerDeathEventType deathType)
	{
		if (!ScriptCallbackExists_PlayerDeath || player == null)
		{
			return;
		}
		RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerDeath callback)
		{
			if (player.ScriptBridge != null)
			{
				callback.Func.Invoke(player.ScriptBridge, new PlayerDeathArgs(deathType));
			}
			return true;
		});
	}

	public void RunScriptOnProjectileHitCallbacks(Projectile projectile, ObjectData hitObject, Microsoft.Xna.Framework.Vector2 deflectionNormal)
	{
		if (!ScriptCallbackExists_ProjectileHit || projectile == null || hitObject == null)
		{
			return;
		}
		int hitObjectID = hitObject.ObjectID;
		bool isPlayer = hitObject.IsPlayer;
		SFDGameScriptInterface.Vector2 hitPosition = projectile.Position.ToSFDVector2();
		SFDGameScriptInterface.Vector2 hitNormal = projectile.HitNormal.ToSFDVector2();
		float hitDamage = projectile.HitDamageValue;
		bool hitIsCrit = projectile.HitDamageCrit;
		bool removeFlag = projectile.HitFlag && deflectionNormal == Microsoft.Xna.Framework.Vector2.Zero;
		bool isDeflection = deflectionNormal != Microsoft.Xna.Framework.Vector2.Zero;
		SFDGameScriptInterface.Vector2 hitDeflectionNormal = deflectionNormal.ToSFDVector2();
		RunScriptCallbacks(delegate(ScriptCallbackEvent_ProjectileHit callback)
		{
			if (projectile.ScriptBridge != null)
			{
				callback.Func.Invoke(projectile.ScriptBridge, new ProjectileHitArgs(hitObjectID, isPlayer, hitDamage, hitIsCrit, hitPosition, hitNormal, removeFlag, isDeflection, hitDeflectionNormal));
			}
			return true;
		});
	}

	public void RunScriptOnProjectileCreatedCallbacks(List<Projectile> projectiles)
	{
		if (ScriptCallbackExists_ProjectileCreated && projectiles != null && projectiles.Count != 0)
		{
			IProjectile[] args = new IProjectile[projectiles.Count];
			for (int i = 0; i < projectiles.Count; i++)
			{
				args[i] = projectiles[i].ScriptBridge;
			}
			RunScriptCallbacks(delegate(ScriptCallbackEvent_ProjectileCreated callback)
			{
				callback.Func.Invoke(args);
				return true;
			});
		}
	}

	public void RunScriptOnExplosionHitCallbacks(ExplosionData explosion)
	{
		if (ScriptCallbackExists_ExplosionHit && explosion != null)
		{
			SFDGameScriptInterface.ExplosionData explosionData = new SFDGameScriptInterface.ExplosionData(explosion.InstanceID, Converter.Box2DToWorld(explosion.ExplosionPosition).ToSFDVector2(), Converter.Box2DToWorld(explosion.ExplosionRadius), explosion.ExplosionDamage);
			explosion.AffectedObjects.RemoveAll((Pair<ObjectData, Explosion> x) => x.ItemA.IsDisposed);
			ExplosionHitArg[] explosionHitArgs = new ExplosionHitArg[explosion.AffectedObjects.Count];
			for (int num = 0; num < explosion.AffectedObjects.Count; num++)
			{
				Pair<ObjectData, Explosion> pair = explosion.AffectedObjects[num];
				explosionHitArgs[num] = new ExplosionHitArg(pair.ItemA.ObjectID, pair.ItemA.ScriptBridge, pair.ItemA.IsPlayer, Converter.Box2DToWorld(pair.ItemB.HitPoint).ToSFDVector2(), (ExplosionHitType)((pair.ItemB.ExplosionDamageTaken != 0f || pair.ItemA.Body.GetType() != Box2D.XNA.BodyType.Static) ? pair.ItemB.HitType : ExplosionData.HitType.None), pair.ItemB.ExplosionDamageTaken, pair.ItemB.ExplosionForceDirection.ToSFDVector2(), pair.ItemB.ExplosionImpactPercentage);
			}
			RunScriptCallbacks(delegate(ScriptCallbackEvent_ExplosionHit callback)
			{
				callback.Func.Invoke(explosionData, explosionHitArgs);
				return true;
			});
		}
	}

	public void QueueRunScriptOnObjectDamageCallbacks(ObjectData objectData, ObjectData.DamageType damageType, float damage, int sourceID)
	{
		if (ScriptCallbackExists_ObjectDamage && objectData != null && !objectData.IsDisposed)
		{
			if (m_queuedRunScriptOnObjectDamageCallbacks == null)
			{
				m_queuedRunScriptOnObjectDamageCallbacks = new List<Tuple<ObjectData, ObjectData.DamageType, float, int>>();
			}
			m_queuedRunScriptOnObjectDamageCallbacks.Add(new Tuple<ObjectData, ObjectData.DamageType, float, int>(objectData, damageType, damage, sourceID));
		}
	}

	public void RunQueuedScriptOnObjectDamageCallbacks()
	{
		if (m_queuedRunScriptOnObjectDamageCallbacks == null || m_queuedRunScriptOnObjectDamageCallbacks.Count <= 0)
		{
			return;
		}
		foreach (Tuple<ObjectData, ObjectData.DamageType, float, int> queuedRunScriptOnObjectDamageCallback in m_queuedRunScriptOnObjectDamageCallbacks)
		{
			RunScriptOnObjectDamageCallbacks(queuedRunScriptOnObjectDamageCallback.Item1, queuedRunScriptOnObjectDamageCallback.Item2, queuedRunScriptOnObjectDamageCallback.Item3, queuedRunScriptOnObjectDamageCallback.Item4);
		}
		m_queuedRunScriptOnObjectDamageCallbacks.Clear();
	}

	public void RunScriptOnObjectDamageCallbacks(ObjectData objectData, ObjectData.DamageType damageType, float damage, int sourceID)
	{
		if (ScriptCallbackExists_ObjectDamage && objectData != null && !objectData.IsDisposed)
		{
			ObjectDamageType scriptDamageType = (ObjectDamageType)damageType;
			bool isPlayer = sourceID != 0 && (scriptDamageType == ObjectDamageType.Player || scriptDamageType == ObjectDamageType.PlayerImpact);
			RunScriptCallbacks(delegate(ScriptCallbackEvent_ObjectDamage callback)
			{
				callback.Func.Invoke(objectData.ScriptBridge, new ObjectDamageArgs(scriptDamageType, damage, isPlayer, sourceID));
				return true;
			});
		}
	}

	public void RunScriptOnObjectTerminatedCallbacks(List<ObjectData> objectDatas)
	{
		if (ScriptCallbackExists_ObjectTerminated && objectDatas != null)
		{
			IObject[] objects = new IObject[objectDatas.Count];
			for (int i = 0; i < objectDatas.Count; i++)
			{
				objects[i] = objectDatas[i].ScriptBridge;
			}
			RunScriptCallbacks(delegate(ScriptCallbackEvent_ObjectTerminated callback)
			{
				callback.Func.Invoke(objects);
				return true;
			});
		}
	}

	public void RunScriptOnObjectCreatedCallbacks(List<ObjectData> objectDatas)
	{
		if (ScriptCallbackExists_ObjectCreated && objectDatas != null)
		{
			IObject[] objects = new IObject[objectDatas.Count];
			for (int i = 0; i < objects.Length; i++)
			{
				objects[i] = objectDatas[i].ScriptBridge;
			}
			RunScriptCallbacks(delegate(ScriptCallbackEvent_ObjectCreated callback)
			{
				callback.Func.Invoke(objects);
				return true;
			});
		}
	}

	public void RunScriptOnPlayerMeleeActionCallbacks(Player player, List<PlayerMeleeHitArg> hitData)
	{
		if (ScriptCallbackExists_PlayerMeleeAction && player != null)
		{
			PlayerMeleeHitArg[] args = new PlayerMeleeHitArg[hitData.Count];
			for (int i = 0; i < args.Length; i++)
			{
				args[i] = hitData[i];
			}
			RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerMeleeAction callback)
			{
				callback.Func.Invoke(player.ScriptBridge, args);
				return true;
			});
		}
	}

	public void RunScriptOnPlayerWeaponRemovedActionCallbacks(Player player, PlayerWeaponRemovedArg arg)
	{
		if (ScriptCallbackExists_PlayerWeaponRemovedAction && player != null)
		{
			RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerWeaponRemovedAction callback)
			{
				callback.Func.Invoke(player.ScriptBridge, arg);
				return true;
			});
		}
	}

	public void RunScriptOnPlayerWeaponAddedActionCallbacks(Player player, PlayerWeaponAddedArg arg)
	{
		if (ScriptCallbackExists_PlayerWeaponAddedAction && player != null)
		{
			RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerWeaponAddedAction callback)
			{
				callback.Func.Invoke(player.ScriptBridge, arg);
				return true;
			});
		}
	}

	public void RunScriptOnPlayerKeyInputCallbacks(Player player, List<VirtualKeyInfo> keyEvents)
	{
		if (ScriptCallbackExists_PlayerKeyInput && player != null && !player.IsRemoved && keyEvents != null && keyEvents.Count != 0)
		{
			VirtualKeyInfo[] args = keyEvents.ToArray();
			RunScriptCallbacks(delegate(ScriptCallbackEvent_PlayerKeyInput callback)
			{
				callback.Func.Invoke(player.ScriptBridge, args);
				return true;
			});
		}
	}

	public void RunScriptUpdateCallbacks()
	{
		if (!ScriptCallbackExists_Update)
		{
			return;
		}
		RunScriptCallbacks(delegate(ScriptCallbackEvent_Update callback)
		{
			float num = ElapsedTotalRealTime - callback.LastElapsedTotalTime;
			if (callback.Interval != 0 && num < (float)callback.Interval)
			{
				return false;
			}
			callback.Func.Invoke(num);
			return true;
		});
	}

	public void RunScriptOnUserMessageCallback(int userIdentifier, string message)
	{
		if (!ScriptCallbackExists_UserMessage || string.IsNullOrWhiteSpace(message))
		{
			return;
		}
		UserScriptBridge userScriptBridge = GetUserScriptBridge(userIdentifier);
		if (userScriptBridge != null)
		{
			RunScriptCallbacks(delegate(ScriptCallbackEvent_UserMessage callback)
			{
				userScriptBridge = GetUserScriptBridge(userIdentifier);
				if (userScriptBridge != null)
				{
					UserMessageCallbackArgs args = new UserMessageCallbackArgs(userScriptBridge, message);
					callback.Func.Invoke(args);
					return true;
				}
				return false;
			});
		}
		else
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"UserIdentifier {userIdentifier} ScriptBridge not found: can't run script message.");
		}
	}

	public void RunOnGameOverTriggers()
	{
		ObjectOnGameOverTrigger[] array = OnGameOverTriggers.ToArray();
		foreach (ObjectOnGameOverTrigger objectOnGameOverTrigger in array)
		{
			if (!objectOnGameOverTrigger.IsDisposed && objectOnGameOverTrigger.CanBeActivated)
			{
				objectOnGameOverTrigger.TriggerNode(null);
			}
		}
	}

	public bool StartExtensionScript(string extensionScriptName, string scriptInstanceID, string script)
	{
		if (ExtensionScripts.ContainsKey(scriptInstanceID))
		{
			return false;
		}
		int num = Interlocked.Increment(ref m_uniqueExtensionScriptInstanceNamePostfix);
		SandboxInstance sandboxInstance = new SandboxInstance(extensionScriptName, scriptInstanceID, $"{scriptInstanceID}_{num}");
		ExtensionScripts.Add(sandboxInstance.ScriptInstanceID, sandboxInstance);
		ExtensionScriptsUniqueID.Add(sandboxInstance.UniqueScriptInstanceID, sandboxInstance.ScriptInstanceID);
		if (StartScript(sandboxInstance, script))
		{
			PrepareLocalStorage(sandboxInstance.ScriptInstanceID);
			try
			{
				CallScriptInstance(sandboxInstance.UniqueScriptInstanceID, "OnStartup", showError: false, null);
			}
			catch (GameWorldCorruptedException)
			{
				return false;
			}
			if (FirstUpdateIsRun)
			{
				try
				{
					CallScriptInstance(sandboxInstance.UniqueScriptInstanceID, "AfterStartup", showError: false, null);
				}
				catch (GameWorldCorruptedException)
				{
					return false;
				}
			}
			return true;
		}
		StopScript(sandboxInstance);
		return false;
	}

	public bool CallScriptInstance(string uniqueScriptInstanceID, string method, params object[] args)
	{
		return CallScriptInstance(uniqueScriptInstanceID, method, showError: true, args);
	}

	public bool CallScriptInstance(string uniqueScriptInstanceID, string method, bool showError, params object[] args)
	{
		SandboxInstance callingScriptFromUniqueInstanceID = GetCallingScriptFromUniqueInstanceID(uniqueScriptInstanceID);
		if (callingScriptFromUniqueInstanceID != null)
		{
			return CallScriptInner(callingScriptFromUniqueInstanceID, method, showError, args);
		}
		ConsoleOutput.ShowMessage(ConsoleOutputType.Warning, $"Script: Script {uniqueScriptInstanceID} is no longer loaded. Can't run method {CreateMethodSignatureString(method, args)}.");
		return false;
	}

	public SandboxInstance GetCallingScriptFromUniqueInstanceID(string uniqueScriptInstanceID)
	{
		SandboxInstance result = null;
		if (string.IsNullOrWhiteSpace(uniqueScriptInstanceID))
		{
			result = DefaultScript;
		}
		else if (ExtensionScriptsUniqueID.ContainsKey(uniqueScriptInstanceID) && ExtensionScripts.ContainsKey(ExtensionScriptsUniqueID[uniqueScriptInstanceID]))
		{
			result = ExtensionScripts[ExtensionScriptsUniqueID[uniqueScriptInstanceID]];
		}
		return result;
	}

	public string GetScriptStorageFilePathFromScriptInstanceID(string scriptInstanceID)
	{
		if (GameInfo == null)
		{
			return "";
		}
		if (string.IsNullOrWhiteSpace(scriptInstanceID))
		{
			if (GameInfo.MapInfo != null)
			{
				return GameInfo.MapInfo.GetScriptStorageFilePath();
			}
		}
		else
		{
			GameInfoScriptInfo gameInfoScriptInfo = GameInfo.GameExtensionScripts.Find((GameInfoScriptInfo x) => x.Key == scriptInstanceID);
			if (gameInfoScriptInfo != null)
			{
				return gameInfoScriptInfo.StorageFilePath;
			}
		}
		return "";
	}

	public bool CallScriptInner(SandboxInstance script, string method, bool showError, params object[] args)
	{
		if (string.IsNullOrWhiteSpace(method))
		{
			return false;
		}
		bool result = true;
		bool flag = false;
		if (script.Sandbox != null && script.Sandbox.ContainsInstance(WorldID))
		{
			if (script.Sandbox.InstanceContainsMethod(WorldID, method, args))
			{
				CallingScriptInstance.Push(script);
				try
				{
					script.Sandbox.ExecuteInstance(WorldID, method, script.SandboxCancellationTokenSource, args);
				}
				catch (SecurityException ex)
				{
					result = false;
					if (m_failedScriptMethods == null)
					{
						m_failedScriptMethods = new HashSet<string>();
					}
					if (!m_failedScriptMethods.Contains(method))
					{
						m_failedScriptMethods.Add(method);
						if (Program.IsGame && m_game.CurrentState == State.EditorTestRun)
						{
							ShowScriptMethodCallError(method, ex, Sandbox.GetArgsTypes(args));
						}
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Script: Method '{CreateMethodSignatureString(method, args)}' security exception in script '{script.ScriptInstanceID}'");
						MessageStack.Show(LanguageHelper.GetText("error.mapScript.scriptMethodSecurityError", CreateMethodSignatureString(method, args), script.ScriptInstanceID), MessageStackType.Error);
						if (ex.InnerException != null)
						{
							MessageStack.Show(ex.InnerException.Message, MessageStackType.Error);
						}
						else
						{
							MessageStack.Show(ex.Message, MessageStackType.Error);
						}
					}
				}
				catch (ThreadAbortException)
				{
				}
				catch (Exception ex3)
				{
					if (ex3 is SandboxExecutionTimeoutException)
					{
						flag = !IsCorrupted;
						IsCorrupted = true;
					}
					result = false;
					if (m_failedScriptMethods == null)
					{
						m_failedScriptMethods = new HashSet<string>();
					}
					if (!m_failedScriptMethods.Contains(method))
					{
						m_failedScriptMethods.Add(method);
						if (Program.IsGame && m_game.CurrentState == State.EditorTestRun)
						{
							ShowScriptMethodCallError(method, ex3, Sandbox.GetArgsTypes(args));
						}
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Script: Method '{CreateMethodSignatureString(method, args)}' error in script '{script.ScriptInstanceID}'");
						MessageStack.Show(LanguageHelper.GetText("error.mapScript.scriptMethodError", CreateMethodSignatureString(method, args), script.ScriptName), MessageStackType.Error);
						if (ex3.InnerException != null)
						{
							MessageStack.Show(ex3.InnerException.Message, MessageStackType.Error);
						}
						else
						{
							MessageStack.Show(ex3.Message, MessageStackType.Error);
						}
					}
				}
				CallingScriptInstance.Pop();
			}
			else
			{
				CallScriptInnerMethodNotFound(script, method, showError, args);
			}
		}
		else
		{
			CallScriptInnerMethodNotFound(script, method, showError, args);
		}
		if (flag)
		{
			throw new GameWorldCorruptedException();
		}
		return result;
	}

	public void CallScriptInnerMethodNotFound(SandboxInstance script, string method, bool showError, params object[] args)
	{
		if (!Program.IsGame || !m_game.CurrentStateHandleScriptInnerMethodNotFound)
		{
			return;
		}
		if (!m_failedScriptCompilation)
		{
			if (m_failedScriptMethods == null)
			{
				m_failedScriptMethods = new HashSet<string>();
			}
			if (!m_failedScriptMethods.Contains(method) && showError)
			{
				m_failedScriptMethods.Add(method);
				if (Program.IsGame && m_game.CurrentState == State.EditorTestRun)
				{
					ShowScriptMethodCallError(method, Sandbox.GetArgsTypes(args));
				}
				MessageStack.Show(LanguageHelper.GetText("error.mapScript.scriptMethodNotFound", CreateMethodSignatureString(method, args), script.ScriptName), MessageStackType.Warning);
				if (!Constants.ENABLE_MAP_SCRIPTS)
				{
					MessageStack.Show(LanguageHelper.GetText("error.mapScript.mapScriptsDisabled"), MessageStackType.Warning);
				}
			}
		}
		if (showError)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Script: Method '{CreateMethodSignatureString(method, args)}' not found in script {script.ScriptName}");
		}
	}

	public void ShowScriptCompileError(string error)
	{
		m_failedScriptCompilation = true;
		StateEditorTest.AddScriptError($"Script Error\r\n{error}");
	}

	public string ShowScriptMethodCallErrorBuildArgsStringFromType(Type paramType)
	{
		string text = paramType.ToString();
		if (text.StartsWith("SFDGameScriptInterface."))
		{
			return text.Remove(0, "SFDGameScriptInterface.".Length);
		}
		return text;
	}

	public string ShowScriptMethodCallErrorBuildArgsString(Type[] paramTypes)
	{
		string text = "";
		if (paramTypes != null)
		{
			for (int i = 0; i < paramTypes.Length; i++)
			{
				text = ((!string.IsNullOrEmpty(text)) ? (text + ", " + ShowScriptMethodCallErrorBuildArgsStringFromType(paramTypes[i])) : ShowScriptMethodCallErrorBuildArgsStringFromType(paramTypes[i]));
			}
		}
		return text;
	}

	public void ShowScriptEventException(ScriptEventException see)
	{
		StateEditorTest.AddScriptError($"Script Error\r\nError in method '{see.method}' in map script. See the exception for more details:\r\n--- Exception ---\r\n{see.message}\r\n{see.formattedStackTrace}");
	}

	public void ShowScriptMethodCallError(string method, Type[] paramTypes)
	{
		StateEditorTest.AddScriptError(string.Format("Script Error\r\nCan't find method '{0}' in map script. Expecting:\r\npublic void {0}({1})\r\n", method, ShowScriptMethodCallErrorBuildArgsString(paramTypes)) + "{\r\n}");
	}

	public void ShowScriptMethodCallError(string method, Exception e, Type[] paramTypes)
	{
		if (e.InnerException != null)
		{
			StateEditorTest.AddScriptError(string.Format("Script Error\r\nError in method '{0}({1})' in map script. See the exception for more details:\r\n--- Exception ---\r\n{2}\r\n{3}", new object[4]
			{
				method,
				ShowScriptMethodCallErrorBuildArgsString(paramTypes),
				e.InnerException.Message,
				e.InnerException.ToString()
			}));
		}
		else
		{
			StateEditorTest.AddScriptError(string.Format("Script Error\r\nError in method '{0}({1})' in map script. See the exception for more details:\r\n--- Exception ---\r\n{2}\r\n{3}", new object[4]
			{
				method,
				ShowScriptMethodCallErrorBuildArgsString(paramTypes),
				e.Message,
				e.ToString()
			}));
		}
	}

	public string CreateMethodSignatureString(string method, params object[] args)
	{
		return $"{method}({ShowScriptMethodCallErrorBuildArgsString(Sandbox.GetArgsTypes(args))})";
	}

	public IEnumerable<SandboxInstance> GetExtensionScripts()
	{
		List<string> list = new List<string>();
		foreach (string key in ExtensionScripts.Keys)
		{
			list.Add(key);
		}
		foreach (string item in list)
		{
			if (ExtensionScripts.ContainsKey(item))
			{
				yield return ExtensionScripts[item];
			}
		}
	}

	public void ScriptsRunOnShutdown()
	{
		if (DefaultScript != null)
		{
			m_scriptsRunOnShutdown.Add(DefaultScript.UniqueScriptInstanceID);
			try
			{
				CallScriptInstance(DefaultScript.UniqueScriptInstanceID, "OnShutdown", showError: false, null);
			}
			catch (Exception)
			{
			}
		}
		foreach (SandboxInstance extensionScript in GetExtensionScripts())
		{
			m_scriptsRunOnShutdown.Add(extensionScript.UniqueScriptInstanceID);
			try
			{
				CallScriptInstance(extensionScript.UniqueScriptInstanceID, "OnShutdown", showError: false, null);
			}
			catch (Exception)
			{
			}
		}
	}

	public void StopScripts()
	{
		StopScript(DefaultScript);
		List<string> list = new List<string>();
		foreach (string key in ExtensionScripts.Keys)
		{
			list.Add(key);
		}
		foreach (string item in list)
		{
			StopScript(ExtensionScripts[item]);
		}
		list.Clear();
		list = null;
		ExtensionScripts.Clear();
		CallingScriptInstance.Clear();
		foreach (UserScriptBridge userScriptBridge in UserScriptBridges)
		{
			userScriptBridge.Dispose();
		}
		UserScriptBridges.Clear();
	}

	public void ClearDrawnScriptDebugInfo()
	{
		if (m_scriptDebugInfoDrawn)
		{
			m_scriptDebugLinesCount = 0;
			m_scriptDebugCirclesCount = 0;
			m_scriptDebugAreasCount = 0;
			m_scriptDebugTextsCount = 0;
		}
	}

	public void MarkScriptDebugInfoDrawn()
	{
		m_scriptDebugInfoDrawn = true;
	}

	public void ScriptDebugDrawLine(SFDGameScriptInterface.Vector2 p1, SFDGameScriptInterface.Vector2 p2, SFDGameScriptInterface.Color c)
	{
		if (m_game.CurrentState == State.EditorTestRun)
		{
			if (m_scriptDebugLines == null)
			{
				m_scriptDebugLines = new ScriptDebugLine[512];
			}
			if (m_scriptDebugLinesCount < 512 && p1.IsValid() && p2.IsValid())
			{
				m_scriptDebugLines[m_scriptDebugLinesCount] = new ScriptDebugLine(Converter.WorldToBox2D(p1.ToXNAVector2()), Converter.WorldToBox2D(p2.ToXNAVector2()), c.ToXNAColor());
				m_scriptDebugLinesCount++;
			}
		}
	}

	public void ScriptDebugDrawCircle(SFDGameScriptInterface.Vector2 p1, float r, SFDGameScriptInterface.Color c)
	{
		if (m_game.CurrentState == State.EditorTestRun)
		{
			if (r < 0.1f)
			{
				r = 0.1f;
			}
			if (m_scriptDebugCircles == null)
			{
				m_scriptDebugCircles = new ScriptDebugCircle[512];
			}
			if (m_scriptDebugCirclesCount < 512 && p1.IsValid())
			{
				m_scriptDebugCircles[m_scriptDebugCirclesCount] = new ScriptDebugCircle(Converter.WorldToBox2D(p1.ToXNAVector2()), Converter.WorldToBox2D(r), c.ToXNAColor());
				m_scriptDebugCirclesCount++;
			}
		}
	}

	public void ScriptDebugDrawArea(SFDGameScriptInterface.Area area, SFDGameScriptInterface.Color c)
	{
		if (m_game.CurrentState == State.EditorTestRun && !area.IsEmpty)
		{
			if (m_scriptDebugAreas == null)
			{
				m_scriptDebugAreas = new ScriptDebugArea[256];
			}
			if (m_scriptDebugAreasCount < 256)
			{
				AABB aabb = area.ToBox2DAABB();
				aabb.lowerBound = Converter.WorldToBox2D(aabb.lowerBound);
				aabb.upperBound = Converter.WorldToBox2D(aabb.upperBound);
				m_scriptDebugAreas[m_scriptDebugAreasCount] = new ScriptDebugArea(aabb, c.ToXNAColor());
				m_scriptDebugAreasCount++;
			}
		}
	}

	public void ScriptDebugDrawText(string text, SFDGameScriptInterface.Vector2 position, SFDGameScriptInterface.Color color)
	{
		if (m_game.CurrentState == State.EditorTestRun && !string.IsNullOrWhiteSpace(text))
		{
			if (m_scriptDebugTexts == null)
			{
				m_scriptDebugTexts = new ScriptDebugText[128];
			}
			if (m_scriptDebugTextsCount < 128 && position.IsValid())
			{
				m_scriptDebugTexts[m_scriptDebugTextsCount] = new ScriptDebugText(text, Converter.WorldToBox2D(position.ToXNAVector2()), color.ToXNAColor());
				m_scriptDebugTextsCount++;
			}
		}
	}

	public void PrepareLocalStorage(string key)
	{
		string scriptStorageFilePathFromScriptInstanceID = GetScriptStorageFilePathFromScriptInstanceID(key);
		if (!string.IsNullOrEmpty(scriptStorageFilePathFromScriptInstanceID) && File.Exists(scriptStorageFilePathFromScriptInstanceID))
		{
			GetStorage(key, StorageMode.Local, null);
		}
	}

	public LocalStorage GetStorage(string key, StorageMode mode, string sharedName)
	{
		LocalStorage value = null;
		switch (mode)
		{
		case StorageMode.Shared:
			if (!string.IsNullOrWhiteSpace(sharedName))
			{
				if (m_sharedStorageEntries == null)
				{
					m_sharedStorageEntries = new Dictionary<string, LocalStorage>();
				}
				sharedName = sharedName.ToLowerInvariant();
				if (!m_sharedStorageEntries.TryGetValue(sharedName, out value))
				{
					string filePath = Path.Combine(Constants.Paths.UserDocumentsCacheScriptDataPath, "Shared", sharedName + ".txt");
					value = new LocalStorage(StorageMode.Shared, filePath);
					PrepareStorage(value);
					m_sharedStorageEntries.Add(sharedName, value);
				}
			}
			break;
		case StorageMode.Session:
			if (m_sessionStorageEntries == null)
			{
				m_sessionStorageEntries = new Dictionary<string, LocalStorage>();
			}
			if (!m_sessionStorageEntries.TryGetValue(key, out value))
			{
				value = new LocalStorage(StorageMode.Session, "");
				m_sessionStorageEntries.Add(key, value);
			}
			break;
		case StorageMode.Local:
			if (m_permanentStorageEntries == null)
			{
				m_permanentStorageEntries = new Dictionary<string, LocalStorage>();
			}
			if (!m_permanentStorageEntries.TryGetValue(key, out value))
			{
				string scriptStorageFilePathFromScriptInstanceID = GetScriptStorageFilePathFromScriptInstanceID(key);
				value = new LocalStorage(StorageMode.Local, scriptStorageFilePathFromScriptInstanceID);
				PrepareStorage(value);
				m_permanentStorageEntries.Add(key, value);
			}
			break;
		}
		return value;
	}

	public void PrepareStorage(LocalStorage storage)
	{
		if (string.IsNullOrEmpty(storage.FilePath))
		{
			return;
		}
		if (m_pendingStorageSaves.Count > 0)
		{
			int num = 800;
			while (m_pendingStorageSaves.Count > 0)
			{
				Thread.Sleep(100);
				num -= 100;
			}
		}
		m_pendingStorageLoad = storage;
		m_pendingStorageIOProcessWH.Set();
		m_pendingStorageLoadWH.WaitOne(5000);
	}

	public static void ReadStorageFromFile(LocalStorage storage)
	{
		try
		{
			if (File.Exists(storage.FilePath))
			{
				if (new FileInfo(storage.FilePath).Length < 12582912L)
				{
					Constants.SetThreadCultureInfo();
					string[] lines = File.ReadAllLines(storage.FilePath, Encoding.UTF8);
					storage.LoadData(lines);
				}
			}
			else
			{
				ConsoleOutput.ShowMessage(ConsoleOutputType.Error, string.Format("Failed to read script data (File not found) from '" + storage.FilePath + "'", Array.Empty<object>()));
			}
		}
		catch (Exception ex)
		{
			ConsoleOutput.ShowMessage(ConsoleOutputType.Error, string.Format("Failed to read script data (error) from '" + storage.FilePath + "' with error: " + ex.Message, Array.Empty<object>()));
		}
	}

	public void ClearStorage(string key)
	{
		if (m_sessionStorageEntries != null && m_sessionStorageEntries.ContainsKey(key))
		{
			m_sessionStorageEntries.Remove(key);
		}
		if (m_permanentStorageEntries != null && m_permanentStorageEntries.ContainsKey(key))
		{
			CheckStorageSaveUnsavedChanges(key, StorageMode.Local, null);
			m_permanentStorageEntries.Remove(key);
		}
	}

	public static void ProcessStorageIOQueue()
	{
		Constants.SetThreadCultureInfo();
		if (m_responsiblePendingStorageManagedThreadId == 0)
		{
			m_responsiblePendingStorageManagedThreadId = Thread.CurrentThread.ManagedThreadId;
		}
		try
		{
			while (!GameSFD.Closing)
			{
				if (m_pendingStorageOutputQueue.Count == 0 && m_pendingStorageLoad == null)
				{
					m_pendingStorageIOProcessWH.WaitOne(30000);
				}
				else
				{
					Thread.Sleep(5);
				}
				if (m_pendingStorageLoad != null)
				{
					lock (m_sessionStorageLock)
					{
						ReadStorageFromFile(m_pendingStorageLoad);
						m_pendingStorageLoad = null;
						m_pendingStorageLoadWH.Set();
					}
				}
				if (m_pendingStorageOutputQueue.Count <= 0 || m_pendingStorageOutputRunning)
				{
					continue;
				}
				m_pendingStorageOutputRunning = true;
				PendingStorageOutput pendingIO = m_pendingStorageOutputQueue.Dequeue();
				if (pendingIO == null || string.IsNullOrEmpty(pendingIO.FilePath))
				{
					continue;
				}
				Task.Factory.StartNew(delegate
				{
					try
					{
						lock (m_sessionStorageLock)
						{
							if (pendingIO.Output != null)
							{
								Constants.SetThreadCultureInfo();
								string directoryName = Path.GetDirectoryName(pendingIO.FilePath);
								if (!Directory.Exists(directoryName))
								{
									Directory.CreateDirectory(directoryName);
								}
								File.WriteAllLines(pendingIO.FilePath, pendingIO.Output.Lines, Encoding.UTF8);
							}
							else if (File.Exists(pendingIO.FilePath))
							{
								File.Delete(pendingIO.FilePath);
							}
						}
					}
					catch (Exception ex2)
					{
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Failed to save script data to file '{pendingIO.FilePath}' with error: {ex2.Message}");
					}
					finally
					{
						m_pendingStorageOutputRunning = false;
						m_pendingStorageIOProcessWH.Set();
					}
				});
			}
		}
		catch (Exception ex)
		{
			if (!(ex is ThreadAbortException))
			{
				Program.ShowError(ex, "Error: StorageIO Thread");
			}
		}
	}

	public void CheckStorageSaveUnsavedChanges(string key, StorageMode mode, string sharedName)
	{
		LocalStorage storage = null;
		if (mode == StorageMode.Local && m_permanentStorageEntries != null)
		{
			m_permanentStorageEntries.TryGetValue(key, out storage);
		}
		else if (mode == StorageMode.Shared && m_sharedStorageEntries != null && !string.IsNullOrWhiteSpace(sharedName))
		{
			m_sharedStorageEntries.TryGetValue(sharedName, out storage);
		}
		if (storage == null || !storage.HasUnsavedChanges || storage.SavingPending)
		{
			return;
		}
		lock (m_pendingStorageSavesLock)
		{
			m_pendingStorageSaves.Add(storage);
		}
		storage.SavingPending = true;
		storage.SavingPendingToFile = storage.FilePath;
		Task.Delay(100).ContinueWith(delegate
		{
			string savingPendingToFile = storage.SavingPendingToFile;
			storage.SavingPendingToFile = "";
			storage.SavingPending = false;
			try
			{
				if (!string.IsNullOrEmpty(savingPendingToFile))
				{
					try
					{
						if (storage.HasData())
						{
							Constants.SetThreadCultureInfo();
							LocalStorageOutput localStorageOutput = new LocalStorageOutput(10485760);
							storage.SaveData(localStorageOutput);
							lock (m_sessionStorageLock)
							{
								m_pendingStorageOutputQueue.Enqueue(new PendingStorageOutput(savingPendingToFile, localStorageOutput));
							}
							m_pendingStorageIOProcessWH.Set();
						}
						else
						{
							lock (m_sessionStorageLock)
							{
								m_pendingStorageOutputQueue.Enqueue(new PendingStorageOutput(savingPendingToFile, null));
							}
							m_pendingStorageIOProcessWH.Set();
						}
						return;
					}
					catch (Exception ex)
					{
						ConsoleOutput.ShowMessage(ConsoleOutputType.Error, $"Failed to save script data to file '{savingPendingToFile}' with error: {ex.Message}");
						return;
					}
				}
			}
			finally
			{
				lock (m_pendingStorageSavesLock)
				{
					m_pendingStorageSaves.Remove(storage);
				}
			}
		});
	}

	public string GetSessionData(string key)
	{
		if (m_sessionDataEntries != null && m_sessionDataEntries.ContainsKey(key))
		{
			return m_sessionDataEntries[key];
		}
		return "";
	}

	public void SetSessionData(string key, string value)
	{
		if (m_sessionDataEntries == null)
		{
			m_sessionDataEntries = new Dictionary<string, string>();
		}
		if (m_sessionDataEntries.ContainsKey(key))
		{
			m_sessionDataEntries[key] = value;
		}
		else
		{
			m_sessionDataEntries.Add(key, value);
		}
	}

	public void ClearSessionData(string key)
	{
		if (m_sessionDataEntries != null && m_sessionDataEntries.ContainsKey(key))
		{
			m_sessionDataEntries.Remove(key);
		}
	}

	public ScriptCallbackEvent CreateScriptCallbackWrapper(Events.CallbackDelegate func)
	{
		if (func is Events.UpdateCallback)
		{
			ScriptCallbackExists_Update = true;
			return new ScriptCallbackEvent_Update(CurrentActiveScriptInstanceUniqueID, (Events.UpdateCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerDeathCallback)
		{
			ScriptCallbackExists_PlayerDeath = true;
			return new ScriptCallbackEvent_PlayerDeath(CurrentActiveScriptInstanceUniqueID, (Events.PlayerDeathCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerDamageCallback)
		{
			ScriptCallbackExists_PlayerDamage = true;
			return new ScriptCallbackEvent_PlayerDamage(CurrentActiveScriptInstanceUniqueID, (Events.PlayerDamageCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerCreatedCallback)
		{
			ScriptCallbackExists_PlayerCreated = true;
			return new ScriptCallbackEvent_PlayerCreated(CurrentActiveScriptInstanceUniqueID, (Events.PlayerCreatedCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.UserMessageCallback)
		{
			ScriptCallbackExists_UserMessage = true;
			return new ScriptCallbackEvent_UserMessage(CurrentActiveScriptInstanceUniqueID, (Events.UserMessageCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.ProjectileHitCallback)
		{
			ScriptCallbackExists_ProjectileHit = true;
			return new ScriptCallbackEvent_ProjectileHit(CurrentActiveScriptInstanceUniqueID, (Events.ProjectileHitCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.ProjectileCreatedCallback)
		{
			ScriptCallbackExists_ProjectileCreated = true;
			return new ScriptCallbackEvent_ProjectileCreated(CurrentActiveScriptInstanceUniqueID, (Events.ProjectileCreatedCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.ExplosionHitCallback)
		{
			ScriptCallbackExists_ExplosionHit = true;
			return new ScriptCallbackEvent_ExplosionHit(CurrentActiveScriptInstanceUniqueID, (Events.ExplosionHitCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.ObjectDamageCallback)
		{
			ScriptCallbackExists_ObjectDamage = true;
			return new ScriptCallbackEvent_ObjectDamage(CurrentActiveScriptInstanceUniqueID, (Events.ObjectDamageCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.ObjectTerminatedCallback)
		{
			ScriptCallbackExists_ObjectTerminated = true;
			return new ScriptCallbackEvent_ObjectTerminated(CurrentActiveScriptInstanceUniqueID, (Events.ObjectTerminatedCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.ObjectCreatedCallback)
		{
			ScriptCallbackExists_ObjectCreated = true;
			return new ScriptCallbackEvent_ObjectCreated(CurrentActiveScriptInstanceUniqueID, (Events.ObjectCreatedCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerMeleeActionCallback)
		{
			ScriptCallbackExists_PlayerMeleeAction = true;
			return new ScriptCallbackEvent_PlayerMeleeAction(CurrentActiveScriptInstanceUniqueID, (Events.PlayerMeleeActionCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerWeaponRemovedActionCallback)
		{
			ScriptCallbackExists_PlayerWeaponRemovedAction = true;
			return new ScriptCallbackEvent_PlayerWeaponRemovedAction(CurrentActiveScriptInstanceUniqueID, (Events.PlayerWeaponRemovedActionCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerWeaponAddedActionCallback)
		{
			ScriptCallbackExists_PlayerWeaponAddedAction = true;
			return new ScriptCallbackEvent_PlayerWeaponAddedAction(CurrentActiveScriptInstanceUniqueID, (Events.PlayerWeaponAddedActionCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.PlayerKeyInputCallback)
		{
			ScriptCallbackExists_PlayerKeyInput = true;
			return new ScriptCallbackEvent_PlayerKeyInput(CurrentActiveScriptInstanceUniqueID, (Events.PlayerKeyInputCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.UserJoinCallback)
		{
			ScriptCallbackExists_UserJoin = true;
			return new ScriptCallbackEvent_UserJoin(CurrentActiveScriptInstanceUniqueID, (Events.UserJoinCallback)func, ElapsedTotalRealTime);
		}
		if (func is Events.UserLeaveCallback)
		{
			ScriptCallbackExists_UserLeave = true;
			return new ScriptCallbackEvent_UserLeave(CurrentActiveScriptInstanceUniqueID, (Events.UserLeaveCallback)func, ElapsedTotalRealTime);
		}
		return new ScriptCallbackEvent(CurrentActiveScriptInstanceUniqueID, func, ElapsedTotalRealTime);
	}

	public void InitGameTimers()
	{
		Timers = new List<GameTimer>();
		m_lastTimerUpdate = 0f;
		m_mainTimer = null;
		m_suddenDeathTimer = null;
	}

	public void CheckMainTimer()
	{
		if (GameOwner == GameOwnerEnum.Client || IgnoreTimers)
		{
			return;
		}
		GameInfo gameInfo = GameInfo;
		if (gameInfo == null || !gameInfo.MapTypeIsTimeLimitEnabled(gameInfo.MapInfo.TypedMapType))
		{
			return;
		}
		if (m_mainTimerActive != gameInfo.TimeLimit.Active)
		{
			UpdateCreateAndCheckTimer(1, gameInfo.TimeLimit.TimeLimit * 1000, !gameInfo.TimeLimit.Active);
			m_mainTimerActive = gameInfo.TimeLimit.Active;
		}
		if (m_forcedSuddenDeathStarted)
		{
			return;
		}
		if (gameInfo.TimeLimit.SuddenDeathEnabled)
		{
			if (!m_suddenDeathTwoOpponentsRemainingCheckPerformed && GameInfo.GameSlots.Where((GameSlot q) => q.IsOccupied).Count() > 2 && CheckTwoOpponentsRemaining())
			{
				m_suddenDeathTwoOpponentsRemainingCheckPerformed = true;
				StartSuddenDeath();
			}
		}
		else
		{
			m_suddenDeathTwoOpponentsRemainingCheckPerformed = false;
			if (m_suddenDeathTimer != null)
			{
				UpdateCreateAndCheckTimer(2, m_suddenDeathTimer.TimeRemainingMs, remove: true);
			}
		}
	}

	public void StartSuddenDeathForced(int suddenDeathSeconds = 45)
	{
		if (!SuddenDeathActive && !m_suddenDeathTwoOpponentsRemainingCheckPerformed)
		{
			m_forcedSuddenDeathStarted = true;
			m_suddenDeathTwoOpponentsRemainingCheckPerformed = true;
			UpdateCreateAndCheckTimer(2, suddenDeathSeconds * 1000, remove: false);
			StartSuddenDeath(suddenDeathSeconds);
		}
	}

	public void StartSuddenDeath(int suddenDeathSeconds = 45)
	{
		float num = (float)suddenDeathSeconds * 1000f;
		if (m_mainTimer != null)
		{
			if (m_mainTimer.TimeRemainingMs < num)
			{
				num = m_mainTimer.TimeRemainingMs;
			}
			else
			{
				m_mainTimer.UpdateTimeRemaining(num);
			}
		}
		if (!m_suddenDeathSpawnFrenzyPerformed)
		{
			WeaponSpawnManager.SpawnFrenzy(Constants.RANDOM.Next(4, 6));
			m_suddenDeathSpawnFrenzyPerformed = true;
		}
		UpdateCreateAndCheckTimer(2, num, remove: false);
	}

	public bool CheckTwoOpponentsRemaining()
	{
		Team team = Team.Independent;
		Team team2 = Team.Independent;
		int num = 0;
		foreach (Player player in Players)
		{
			if (!player.IsDead)
			{
				num++;
				switch (num)
				{
				case 1:
					team = player.CurrentTeam;
					break;
				case 2:
					team2 = player.CurrentTeam;
					break;
				case 3:
					return false;
				}
			}
		}
		if (num == 2)
		{
			if (team != Team.Independent)
			{
				return team != team2;
			}
			return true;
		}
		return false;
	}

	public bool CheckOpponentsRemaining()
	{
		Team team = Team.Independent;
		Team team2 = Team.Independent;
		bool flag = true;
		foreach (Player player in Players)
		{
			if (player.IsDead)
			{
				continue;
			}
			if (flag)
			{
				team = player.CurrentTeam;
				flag = false;
				continue;
			}
			team2 = player.CurrentTeam;
			if (team != Team.Independent && team == team2)
			{
				continue;
			}
			return true;
		}
		return false;
	}

	public void UpdateCreateAndCheckTimer(int id, float timeRemainingMs, bool remove)
	{
		bool flag = false;
		foreach (GameTimer timer in Timers)
		{
			if (timer.ID == id)
			{
				flag = true;
				float timeRemainingMs2 = timeRemainingMs;
				if (GameOwner == GameOwnerEnum.Client && GameSFD.Handle.Client != null)
				{
					timeRemainingMs2 = Math.Max(timeRemainingMs - GameSFD.Handle.Client.GetAverageRoundtripTime(), 1f);
				}
				timer.UpdateTimeRemaining(timeRemainingMs2);
				if (remove)
				{
					timer.IsOver = true;
				}
			}
		}
		if (!flag && !remove && timeRemainingMs > 1000f && !IgnoreTimers)
		{
			GameTimer gameTimer = null;
			switch (id)
			{
			case 1:
				gameTimer = new GameTimerMain(timeRemainingMs, this);
				m_mainTimer = (GameTimerMain)gameTimer;
				break;
			case 2:
				gameTimer = new GameTimerSuddenDeath(timeRemainingMs, this);
				m_suddenDeathTimer = (GameTimerSuddenDeath)gameTimer;
				break;
			default:
				gameTimer = new GameTimer(id, this, timeRemainingMs, "Timer");
				break;
			}
			Timers.Add(gameTimer);
		}
	}

	public void UpdateGameTimers(float ms)
	{
		GameTimerMain gameTimerMain = null;
		GameTimerSuddenDeath suddenDeathTimer = null;
		List<GameTimer> list = null;
		for (int num = Timers.Count - 1; num >= 0; num--)
		{
			GameTimer gameTimer = Timers[num];
			gameTimer.Update(ms);
			if (gameTimer.ID == 1)
			{
				gameTimerMain = (GameTimerMain)gameTimer;
			}
			if (gameTimer.ID == 2)
			{
				suddenDeathTimer = (GameTimerSuddenDeath)gameTimer;
			}
			if (gameTimer.IsOver || gameTimer.Changed)
			{
				gameTimer.Changed = false;
				if (gameTimer.IsOver)
				{
					Timers.RemoveAt(num);
					gameTimer.Dispose();
					if (gameTimerMain == gameTimer)
					{
						gameTimerMain = null;
					}
				}
				if (list == null)
				{
					list = new List<GameTimer>();
				}
				list.Add(gameTimer);
			}
		}
		if (GameOwner == GameOwnerEnum.Server)
		{
			if (list != null)
			{
				m_game.Server.SendMessage(MessageType.GameTimers, list);
			}
			else if (Math.Abs(m_lastTimerUpdate - ElapsedTotalRealTime) > 10000f)
			{
				m_lastTimerUpdate = ElapsedTotalRealTime;
				m_game.Server.SendMessage(MessageType.GameTimers, Timers);
			}
		}
		m_mainTimer = gameTimerMain;
		m_suddenDeathTimer = suddenDeathTimer;
	}

	public void DisposeGameTimers()
	{
		foreach (GameTimer timer in Timers)
		{
			timer.Dispose();
		}
		Timers.Clear();
		Timers = null;
	}

	public void InitQueueTriggers()
	{
		m_queuedTriggers = new Queue<Pair<ObjectTriggerBase, BaseObject>>();
		m_activatedTriggers = new Dictionary<int, HashSet<BaseObject>>();
		m_queuedTriggersWithSenders = new Dictionary<int, HashSet<BaseObject>>();
	}

	public void DisposeQueueTriggers()
	{
		m_queuedTriggers.Clear();
		m_queuedTriggers = null;
		m_activatedTriggers.Clear();
		m_activatedTriggers = null;
		m_queuedTriggersWithSenders.Clear();
		m_queuedTriggersWithSenders = null;
	}

	public void HandleQueuedTriggers(float ms)
	{
		if (m_activatedTriggers.Count > 0)
		{
			m_activatedTriggers.Clear();
		}
		if (m_queuedTriggers.Count <= 0)
		{
			return;
		}
		List<Pair<ObjectTriggerBase, BaseObject>> list = new List<Pair<ObjectTriggerBase, BaseObject>>();
		while (m_queuedTriggers.Count > 0)
		{
			list.Add(m_queuedTriggers.Dequeue());
		}
		m_queuedTriggersWithSenders.Clear();
		foreach (Pair<ObjectTriggerBase, BaseObject> item in list)
		{
			if (item.ItemA.IsDisposed)
			{
				continue;
			}
			bool flag = false;
			if (item.ItemB is IObject)
			{
				ObjectData objectDataByID = GetObjectDataByID(((IObject)item.ItemB).UniqueID);
				if (objectDataByID == null || objectDataByID.IsDisposed)
				{
					flag = true;
				}
			}
			item.ItemA.TriggerNode(flag ? null : item.ItemB);
		}
		list.Clear();
		list = null;
	}

	public bool CheckTriggerExecution(ObjectTriggerBase trigger, BaseObject sender)
	{
		if (!trigger.CanBeActivated)
		{
			return false;
		}
		HashSet<BaseObject> value = null;
		if (m_activatedTriggers.TryGetValue(trigger.ObjectID, out value))
		{
			if (value.Contains(sender))
			{
				if (m_queuedTriggersWithSenders.TryGetValue(trigger.ObjectID, out value))
				{
					if (value.Contains(sender))
					{
						return false;
					}
					value.Add(sender);
				}
				else
				{
					value = new HashSet<BaseObject>();
					value.Add(sender);
					m_queuedTriggersWithSenders.Add(trigger.ObjectID, value);
				}
				m_queuedTriggers.Enqueue(new Pair<ObjectTriggerBase, BaseObject>(trigger, sender));
				return false;
			}
			value.Add(sender);
			return true;
		}
		value = new HashSet<BaseObject>();
		value.Add(sender);
		m_activatedTriggers.Add(trigger.ObjectID, value);
		return true;
	}

	public void SendObjectPropertyValues()
	{
		if (ObjectPropertyValuesToSend.Count <= 0)
		{
			return;
		}
		List<ObjectPropertyInstance> list = null;
		List<ObjectPropertyInstance> list2 = null;
		foreach (ObjectPropertyInstance item in ObjectPropertyValuesToSend)
		{
			if (item.ObjectOwner == null || item.ObjectOwner.IsDisposed)
			{
				continue;
			}
			if (GameSFD.LastUpdateNetTimeMS - item.LastSyncTimeStamp < 98.0)
			{
				if (list == null)
				{
					list = new List<ObjectPropertyInstance>();
				}
				list.Add(item);
				continue;
			}
			item.LastSyncTimeStamp = GameSFD.LastUpdateNetTimeMS;
			if (item.Base.SyncType == ObjectPropertySyncType.SyncedTCP)
			{
				NewObjectData newObjectData = new NewObjectData(NewObjectType.PropertyUpdate);
				newObjectData.WriteOrder.Add(NewObjectData.ParamType.PropertyInstanceValueChanged);
				newObjectData.Write(item.ObjectOwner.ObjectID);
				newObjectData.Write(item.Base.PropertyID);
				if (item.Base.ValueType == typeof(string))
				{
					newObjectData.Write(0);
					newObjectData.Write((string)item.Value);
				}
				if (item.Base.ValueType == typeof(float))
				{
					newObjectData.Write(1);
					newObjectData.Write((float)item.Value);
				}
				if (item.Base.ValueType == typeof(int))
				{
					newObjectData.Write(2);
					newObjectData.Write((int)item.Value);
				}
				if (item.Base.ValueType == typeof(bool))
				{
					newObjectData.Write(3);
					newObjectData.Write((bool)item.Value);
				}
				NewObjectsCollection.AddNewObject(newObjectData);
			}
			else if (item.Base.SyncType == ObjectPropertySyncType.SyncedUDP)
			{
				if (list2 == null)
				{
					list2 = new List<ObjectPropertyInstance>();
				}
				list2.Add(item);
			}
		}
		if (list2 != null)
		{
			NetMessage.ObjectProperty.Data[] messageToWrite = new NetMessage.ObjectProperty.Data[list2.Count];
			for (int i = 0; i < messageToWrite.Length; i++)
			{
				messageToWrite[i] = new NetMessage.ObjectProperty.Data(list2[i], list2[i].MessageCountSend);
			}
			NetMessage.ObjectProperty.Write(ref messageToWrite, GetMultiPacket());
		}
		ObjectPropertyValuesToSend.Clear();
		if (list != null)
		{
			ObjectPropertyValuesToSend = list;
		}
	}

	public void AddObjectPropertyValueToSend(ObjectPropertyInstance propertyChanged)
	{
		ObjectPropertyValuesToSend.Remove(propertyChanged);
		ObjectPropertyValuesToSend.Add(propertyChanged);
	}

	public void WeatherEnabledUpdated()
	{
		if (Weather != null)
		{
			SFD.Weather.Weather weather = Weather;
			Weather = null;
			weather.Remove();
		}
		SFD.Weather.Weather weather2 = null;
		if (Constants.ENABLE_WEATHER)
		{
			string text = (string)PropertiesWorld.Get(ObjectPropertyID.World_Weather).Value;
			WeatherTypeEnum[] values = EnumUtil.GetValues<WeatherTypeEnum>();
			for (int i = 0; i < values.Length; i++)
			{
				WeatherTypeEnum weatherType = values[i];
				if (weatherType.ToString() == text)
				{
					weather2 = WeatherCreate.CreateNewWeather(weatherType);
					break;
				}
			}
		}
		if (weather2 != null)
		{
			weather2.SetGameWorld(this);
			Weather = weather2;
			WeatherUpdateWholeCollision();
		}
	}

	public void WeatherRemoveObject(ObjectData od)
	{
		if (Weather == null || od.Tile.MainLayer != 1)
		{
			return;
		}
		Body body = od.Owners[0].GetBody();
		body.GetAABB(out var aabb);
		aabb.lowerBound.Y = Converter.ConvertWorldToBox2D(BoundsWorldBottom);
		float num = Converter.ConvertBox2DToWorld(aabb.lowerBound.X);
		float num2 = Converter.ConvertBox2DToWorld(aabb.upperBound.X);
		if (Math.Abs(num) > 100000000f || !(Math.Abs(num2) <= 100000000f))
		{
			return;
		}
		int num3 = (int)(num / (float)Weather.GetStripWorldSize) * Weather.GetStripWorldSize;
		int num4 = ((int)(num2 / (float)Weather.GetStripWorldSize) + 1) * Weather.GetStripWorldSize;
		if (num3 >= num4)
		{
			return;
		}
		Box2D.XNA.RayCastInput[] rayCastInputs = new Box2D.XNA.RayCastInput[(num4 - num3) / Weather.GetStripWorldSize];
		ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>[] hits = new ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>[rayCastInputs.Length];
		ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>[] sourceHits = new ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>[rayCastInputs.Length];
		float num5 = Converter.ConvertWorldToBox2D(Weather.GetStripWorldSize);
		for (int i = 0; i < rayCastInputs.Length; i++)
		{
			float x = Converter.ConvertWorldToBox2D(num3) + num5 / 2f + (float)i * num5;
			rayCastInputs[i].maxFraction = 1f;
			rayCastInputs[i].p1 = new Microsoft.Xna.Framework.Vector2(x, aabb.upperBound.Y);
			rayCastInputs[i].p2 = new Microsoft.Xna.Framework.Vector2(x, aabb.lowerBound.Y);
			hits[i] = new ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>(item1: false, 1f, Microsoft.Xna.Framework.Vector2.Zero, null);
			sourceHits[i] = new ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>(item1: false, 1f, Microsoft.Xna.Framework.Vector2.Zero, null);
		}
		body.GetWorld().QueryAABB(delegate(Fixture fixture)
		{
			if (fixture != null && fixture.GetUserData() != null && fixture.GetBody().GetType() == Box2D.XNA.BodyType.Static && !fixture.IsSensor() && !fixture.IsCloud())
			{
				fixture.GetFilterData(out var filter);
				if (filter.categoryBits > 0)
				{
					bool flag = false;
					foreach (Fixture owner in od.Owners)
					{
						if (fixture == owner)
						{
							flag = true;
							break;
						}
					}
					for (int j = 0; j < rayCastInputs.Length; j++)
					{
						if (fixture.RayCast(out var output, ref rayCastInputs[j]))
						{
							if (flag)
							{
								if (output.fraction < sourceHits[j].Item2)
								{
									sourceHits[j].Item1 = true;
									sourceHits[j].Item2 = output.fraction;
									sourceHits[j].Item3 = output.normal;
									sourceHits[j].Item4 = fixture;
								}
							}
							else
							{
								ObjectData objectData = ObjectData.Read(fixture);
								if (objectData != null && objectData.Tile != null && objectData.Tile.WeatherStop && output.fraction < hits[j].Item2)
								{
									hits[j].Item1 = true;
									hits[j].Item2 = output.fraction;
									hits[j].Item3 = output.normal;
									hits[j].Item4 = fixture;
								}
							}
						}
					}
				}
			}
			return true;
		}, ref aabb);
		for (int num6 = 0; num6 < rayCastInputs.Length; num6++)
		{
			if (sourceHits[num6].Item1)
			{
				Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(rayCastInputs[num6].GetHitPosition(sourceHits[num6].Item2));
				if (hits[num6].Item1)
				{
					ObjectData odHit = ObjectData.Read(hits[num6].Item4);
					Weather.SetWorldHeightIfFromSource(vector.X, vector.Y, Converter.ConvertBox2DToWorld(rayCastInputs[num6].GetHitPosition(hits[num6].Item2).Y), 2f, hits[num6].Item3, odHit);
				}
				else
				{
					Weather.RemoveWorldHeightIfFromSource(vector.X, vector.Y, 2f);
				}
			}
		}
	}

	public void WeatherUpdateWholeCollision()
	{
		if (Weather == null)
		{
			return;
		}
		for (Body body = b2_world_active.GetBodyList(); body != null; body = body.GetNext())
		{
			if (body != null && body.GetUserData() != null && body.GetType() == Box2D.XNA.BodyType.Static)
			{
				WeatherSetCollisionAgainstBody(body);
			}
		}
	}

	public void WeatherSetCollisionAgainstBody(Body body)
	{
		if (Weather == null || body.GetType() != Box2D.XNA.BodyType.Static)
		{
			return;
		}
		body.GetAABB(out var aabb);
		aabb.Grow(0.1f);
		float num = Converter.ConvertBox2DToWorld(aabb.lowerBound.X);
		float num2 = Converter.ConvertBox2DToWorld(aabb.upperBound.X);
		if (Math.Abs(num) > 100000000f || !(Math.Abs(num2) <= 100000000f))
		{
			return;
		}
		int num3 = (int)(num / (float)Weather.GetStripWorldSize) * Weather.GetStripWorldSize;
		int num4 = ((int)(num2 / (float)Weather.GetStripWorldSize) + 1) * Weather.GetStripWorldSize;
		if (num3 >= num4)
		{
			return;
		}
		Box2D.XNA.RayCastInput[] array = new Box2D.XNA.RayCastInput[(num4 - num3) / Weather.GetStripWorldSize];
		Box2D.XNA.RayCastInput[] obj = new Box2D.XNA.RayCastInput[2]
		{
			default(Box2D.XNA.RayCastInput),
			default(Box2D.XNA.RayCastInput)
		};
		obj[0].maxFraction = 1f;
		obj[1] = default(Box2D.XNA.RayCastInput);
		obj[1].maxFraction = 1f;
		ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>[] array2 = new ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>[array.Length];
		float num5 = Converter.ConvertWorldToBox2D(Weather.GetStripWorldSize);
		for (int i = 0; i < array.Length; i++)
		{
			float x = Converter.ConvertWorldToBox2D(num3) + num5 / 2f + (float)i * num5;
			array[i].maxFraction = 1f;
			array[i].p1 = new Microsoft.Xna.Framework.Vector2(x, aabb.upperBound.Y);
			array[i].p2 = new Microsoft.Xna.Framework.Vector2(x, aabb.lowerBound.Y);
			array2[i] = new ItemContainer<bool, float, Microsoft.Xna.Framework.Vector2, Fixture>(item1: false, 1f, Microsoft.Xna.Framework.Vector2.Zero, null);
		}
		for (Fixture fixture = body.GetFixtureList(); fixture != null; fixture = fixture.GetNext())
		{
			if (fixture != null && fixture.GetUserData() != null && fixture.GetBody().GetType() == Box2D.XNA.BodyType.Static && !fixture.IsSensor() && !fixture.IsCloud())
			{
				fixture.GetFilterData(out var filter);
				if (filter.categoryBits > 0)
				{
					ObjectData objectData = ObjectData.Read(fixture);
					if (objectData != null && objectData.Tile != null && objectData.Tile.WeatherStop)
					{
						for (int j = 0; j < array.Length; j++)
						{
							if (fixture.RayCast(out var output, ref array[j]) && output.fraction < array2[j].Item2)
							{
								array2[j].Item1 = true;
								array2[j].Item2 = output.fraction;
								array2[j].Item3 = output.normal;
								array2[j].Item4 = fixture;
							}
						}
					}
				}
			}
		}
		for (int k = 0; k < array.Length; k++)
		{
			if (array2[k].Item1)
			{
				Microsoft.Xna.Framework.Vector2 vector = Converter.ConvertBox2DToWorld(array[k].GetHitPosition(array2[k].Item2));
				ObjectData odHit = ObjectData.Read(array2[k].Item4);
				Weather.SetWorldHeight(vector.X, vector.Y, setOnlyIfHeigher: true, array2[k].Item3, odHit);
			}
		}
	}

	[Obsolete("Replace - Use Player.Contacts information isntead")]
	public bool CompareFeetPositions(Microsoft.Xna.Framework.Vector2 plrPos1, Microsoft.Xna.Framework.Vector2 plrPos2, ref Filter collisionFilter)
	{
		Player.GetAABBFeet(out var aabb, plrPos1);
		Player.GetAABBFeet(out var aabb2, plrPos2);
		bool[] array = new bool[2];
		bool[] array2 = new bool[2];
		CompareFeetPositionsCheck(ref aabb, collisionFilter, out array[0], out array2[0]);
		CompareFeetPositionsCheck(ref aabb2, collisionFilter, out array[1], out array2[1]);
		if (array[0] == array2[0])
		{
			return array[1] == array2[1];
		}
		return false;
	}

	public void CompareFeetPositionsCheck(ref AABB aabb, Filter collisionFilter, out bool leftSupport, out bool rightSupport)
	{
		bool lSupport = false;
		bool rSupport = false;
		Box2D.XNA.RayCastInput[] rayCastInputs = new Box2D.XNA.RayCastInput[2];
		for (int i = 0; i < 2; i++)
		{
			rayCastInputs[i] = default(Box2D.XNA.RayCastInput);
			rayCastInputs[i].maxFraction = 1f;
		}
		rayCastInputs[0].p1 = new Microsoft.Xna.Framework.Vector2(aabb.lowerBound.X, aabb.upperBound.Y);
		rayCastInputs[0].p2 = new Microsoft.Xna.Framework.Vector2(aabb.lowerBound.X, aabb.lowerBound.Y - 0.08f);
		rayCastInputs[1].p1 = new Microsoft.Xna.Framework.Vector2(aabb.upperBound.X, aabb.upperBound.Y);
		rayCastInputs[1].p2 = new Microsoft.Xna.Framework.Vector2(aabb.upperBound.X, aabb.lowerBound.Y - 0.08f);
		b2_world_active.QueryAABB(delegate(Fixture fixture)
		{
			if (!fixture.IsSensor() && fixture.GetUserData() != null && !ObjectData.Read(fixture).IsPlayer)
			{
				fixture.GetFilterData(out var filter);
				if (Settings.b2ShouldCollide(ref collisionFilter, ref filter))
				{
					if (!lSupport && fixture.RayCast(out var output, ref rayCastInputs[0]))
					{
						lSupport = true;
					}
					if (!rSupport && fixture.RayCast(out output, ref rayCastInputs[1]))
					{
						rSupport = true;
					}
					if (lSupport && rSupport)
					{
						return false;
					}
				}
			}
			return true;
		}, ref aabb);
		leftSupport = lSupport;
		rightSupport = rSupport;
	}
}
