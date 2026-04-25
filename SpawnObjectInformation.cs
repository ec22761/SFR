using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace SFD;

public class SpawnObjectInformation
{
	public enum SpawnTypeValue
	{
		Default,
		Static,
		Dynamic
	}

	public Vector2 WorldPosition;

	public ObjectData ObjectData;

	public bool ObjectDataUsed;

	public float Rotation;

	public Vector2 InitialLinearVelocity;

	public float InitialAngularVelocity;

	public int Layer;

	public string[] Colors;

	public List<ObjectProperties.ObjectPropertyValueInstance> PropertyValues;

	public int IgnoreBodyID;

	public ushort GroupID;

	public int PreserveJointsFromObjectID;

	public bool FireSmoking;

	public bool FireBurning;

	public SpawnTypeValue SpawnType;

	public void ObjectIDCompression(Dictionary<int, int> oldToNewValues, bool preserveCurrentValues = false)
	{
		if (PropertyValues == null)
		{
			return;
		}
		foreach (ObjectProperties.ObjectPropertyValueInstance propertyValue in PropertyValues)
		{
			ObjectPropertyItem property = ObjectProperties.GetProperty(propertyValue.PropertyID);
			if (property == null)
			{
				continue;
			}
			if (property.PropertyClass == ObjectPropertyClass.TargetObjectData)
			{
				int num = (int)propertyValue.Value;
				if (oldToNewValues.ContainsKey(num))
				{
					num = oldToNewValues[num];
					propertyValue.Value = num;
				}
				else if (num != 0 && !preserveCurrentValues)
				{
					propertyValue.Value = 0;
				}
			}
			if (property.PropertyClass != ObjectPropertyClass.TargetObjectDataMultiple)
			{
				continue;
			}
			int[] array = Converter.StringToIntArray((string)propertyValue.Value);
			List<int> list = new List<int>();
			int[] array2 = array;
			foreach (int num2 in array2)
			{
				if (oldToNewValues.ContainsKey(num2))
				{
					list.Add(oldToNewValues[num2]);
				}
				else if (preserveCurrentValues)
				{
					list.Add(num2);
				}
			}
			string value = Converter.IntArrayToString(list.ToArray());
			propertyValue.Value = value;
		}
	}

	public List<ObjectProperties.ObjectPropertyValueInstance> CopyPropertyValues()
	{
		if (PropertyValues == null)
		{
			return null;
		}
		List<ObjectProperties.ObjectPropertyValueInstance> list = new List<ObjectProperties.ObjectPropertyValueInstance>(PropertyValues.Count);
		foreach (ObjectProperties.ObjectPropertyValueInstance propertyValue in PropertyValues)
		{
			list.Add(propertyValue.Copy());
		}
		return list;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = 0f;
		ObjectData.FaceDirection = 1;
		InitialLinearVelocity = Vector2.Zero;
		InitialAngularVelocity = 0f;
		Layer = 0;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition, short faceDirection)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = 0f;
		ObjectData.FaceDirection = faceDirection;
		InitialLinearVelocity = Vector2.Zero;
		InitialAngularVelocity = 0f;
		Layer = 0;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition, float rotation)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = rotation;
		ObjectData.FaceDirection = 1;
		InitialLinearVelocity = Vector2.Zero;
		InitialAngularVelocity = 0f;
		Layer = 0;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition, float rotation, short faceDirection)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = rotation;
		ObjectData.FaceDirection = faceDirection;
		InitialLinearVelocity = Vector2.Zero;
		InitialAngularVelocity = 0f;
		Layer = 0;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition, float rotation, short faceDirection, int layer)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = rotation;
		ObjectData.FaceDirection = faceDirection;
		InitialLinearVelocity = Vector2.Zero;
		InitialAngularVelocity = 0f;
		Layer = layer;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition, float rotation, short faceDirection, Vector2 linearVelocity, float angularVelocity)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = rotation;
		ObjectData.FaceDirection = faceDirection;
		InitialLinearVelocity = linearVelocity;
		InitialAngularVelocity = angularVelocity;
		Layer = 0;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public SpawnObjectInformation(ObjectData objectData, Vector2 worldPosition, float rotation, short faceDirection, int layer, Vector2 linearVelocity, float angularVelocity)
	{
		ObjectData = objectData;
		WorldPosition = worldPosition;
		Rotation = rotation;
		ObjectData.FaceDirection = faceDirection;
		InitialLinearVelocity = linearVelocity;
		InitialAngularVelocity = angularVelocity;
		Layer = layer;
		IgnoreBodyID = 0;
		FireSmoking = false;
		FireBurning = false;
		GroupID = 0;
		SpawnType = SpawnTypeValue.Default;
	}

	public static SpawnObjectInformation CreateTemplate(ObjectData sourceObject, string tileKey)
	{
		return new SpawnObjectInformation(sourceObject.GameWorld.CreateObjectData(tileKey), sourceObject.GetWorldPosition(), sourceObject.GetAngle(), sourceObject.FaceDirection, sourceObject.Body.GetLinearVelocity(), sourceObject.Body.GetAngularVelocity())
		{
			PreserveJointsFromObjectID = sourceObject.ObjectID,
			FireSmoking = sourceObject.Fire.IsSmoking,
			FireBurning = sourceObject.Fire.IsBurning,
			Colors = sourceObject.ColorsCopy,
			SpawnType = sourceObject.GetCurrentSpawnType()
		};
	}

	~SpawnObjectInformation()
	{
		ObjectData = null;
		Colors = null;
		if (PropertyValues != null)
		{
			PropertyValues.Clear();
			PropertyValues = null;
		}
	}

	public override string ToString()
	{
		return ObjectData.ToString();
	}
}
