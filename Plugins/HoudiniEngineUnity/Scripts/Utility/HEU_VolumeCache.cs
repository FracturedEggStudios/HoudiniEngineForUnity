﻿/*
* Copyright (c) <2018> Side Effects Software Inc.
* All rights reserved.
*
* Redistribution and use in source and binary forms, with or without
* modification, are permitted provided that the following conditions are met:
*
* 1. Redistributions of source code must retain the above copyright notice,
*    this list of conditions and the following disclaimer.
*
* 2. The name of Side Effects Software may not be used to endorse or
*    promote products derived from this software without specific prior
*    written permission.
*
* THIS SOFTWARE IS PROVIDED BY SIDE EFFECTS SOFTWARE "AS IS" AND ANY EXPRESS
* OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
* OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.  IN
* NO EVENT SHALL SIDE EFFECTS SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT,
* INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
* LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA,
* OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
* LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
* NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
* EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace HoudiniEngineUnity
{
	/////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	// Typedefs (copy these from HEU_Common.cs)
	using HAPI_NodeId = System.Int32;
	using HAPI_PartId = System.Int32;
	using HAPI_StringHandle = System.Int32;

	/// <summary>
	/// Represents a volume-based terrain layer
	/// </summary>
	[System.Serializable]
	public class HEU_VolumeLayer
	{
		public string _layerName;
		public HEU_PartData _part;
		public float _strength = 1.0f;
		public bool _uiExpanded;
		public int _tile = 0;

		[System.NonSerialized]
		public bool _hasLayerAttributes;

#if UNITY_2018_3_OR_NEWER
		// Not keeping reference to TerrainLayer (might revisit if needed).
#else
		// Index of the SplatPrototype in the TerrainData splatprototypes list.
		// For reusing on recook.
		public int _splatPrototypeIndex = -1;
#endif
	}


	/// <summary>
	/// Creates terrain out of volume parts.
	/// </summary>
	public class HEU_VolumeCache : ScriptableObject
	{
		//	DATA ------------------------------------------------------------------------------------------------------

		[SerializeField]
		private HEU_GeoNode _ownerNode;

		[SerializeField]
		private List<HEU_VolumeLayer> _layers = new List<HEU_VolumeLayer>();

		// Used for storing in use layers during update. This is temporary and does not need to be serialized.
		private List<HEU_VolumeLayer> _updatedLayers;

		[SerializeField]
		private int _tileIndex;

		[SerializeField]
		private bool _isDirty;

		public bool IsDirty { get { return _isDirty; } set { _isDirty = value; } }

		[SerializeField]
		private string _geoName;

		[SerializeField]
		private string _objName;

		public int TileIndex { get { return _tileIndex; } }

		public string ObjectName { get { return _objName; } }

		public string GeoName { get { return _geoName; } }

		public bool _uiExpanded = true;

		public bool UIExpanded { get { return _uiExpanded; } set { _uiExpanded = value; } }

		// Hold a reference to the TerrainData so that it can be serialized/deserialized when using presets (Rebuild/duplicate)
		[SerializeField]
		private TerrainData _terrainData;


		//	LOGIC -----------------------------------------------------------------------------------------------------

		public static List<HEU_VolumeCache> UpdateVolumeCachesFromParts(HEU_SessionBase session, HEU_GeoNode ownerNode, List<HEU_PartData> volumeParts, List<HEU_VolumeCache> volumeCaches)
		{
			HEU_HoudiniAsset parentAsset = ownerNode.ParentAsset;

			foreach (HEU_VolumeCache cache in volumeCaches)
			{
				// Remove current volume caches from parent asset.
				// These get added back in below.
				parentAsset.RemoveVolumeCache(cache);

				// Mark the cache for updating
				cache.StartUpdateLayers();
			}

			// This will keep track of volume caches still in use
			List<HEU_VolumeCache> updatedCaches = new List<HEU_VolumeCache>();

			int numParts = volumeParts.Count;
			for (int i = 0; i < numParts; ++i)
			{
				// Get the tile index, if it exists, for this part
				HAPI_AttributeInfo tileAttrInfo = new HAPI_AttributeInfo();
				int[] tileAttrData = new int[0];
				HEU_GeneralUtility.GetAttribute(session, ownerNode.GeoID, volumeParts[i].PartID, HEU_Defines.HAPI_HEIGHTFIELD_TILE_ATTR, ref tileAttrInfo, ref tileAttrData, session.GetAttributeIntData);
				if (tileAttrData != null && tileAttrData.Length > 0)
				{
					//Debug.LogFormat("Tile: {0}", tileAttrData[0]);

					int tile = tileAttrData[0];
					HEU_VolumeCache volumeCache = null;

					// Find cache in updated list
					for (int j = 0; j < updatedCaches.Count; ++j)
					{
						if (updatedCaches[j] != null && updatedCaches[j].TileIndex == tile)
						{
							volumeCache = updatedCaches[j];
							break;
						}
					}

					if (volumeCache != null)
					{
						volumeCache.UpdateLayerFromPart(session, volumeParts[i]);

						// Skip adding new cache since already found in updated list
						continue;
					}

					// Find existing cache in old list
					if (volumeCaches != null && volumeCaches.Count > 0)
					{
						for(int j = 0; j < volumeCaches.Count; ++j)
						{
							if (volumeCaches[j] != null && volumeCaches[j].TileIndex == tile)
							{
								volumeCache = volumeCaches[j];
								break;
							}
						}
					}

					// Create new cache for this tile if not found
					if (volumeCache == null)
					{
						volumeCache = ScriptableObject.CreateInstance<HEU_VolumeCache>();
						volumeCache.Initialize(ownerNode, tile);
						volumeCache.StartUpdateLayers();
					}

					volumeCache.UpdateLayerFromPart(session, volumeParts[i]);

					if (!updatedCaches.Contains(volumeCache))
					{
						updatedCaches.Add(volumeCache);
					}
				}
				else
				{
					// No tile index. Most likely a single terrain tile.

					HEU_VolumeCache volumeCache = null;

					if (updatedCaches.Count == 0)
					{
						// Create a single volume cache, or use existing if it was just 1.
						// If more than 1 volume cache exists, this will recreate a single one

						if (volumeCaches == null || volumeCaches.Count != 1)
						{
							volumeCache = ScriptableObject.CreateInstance<HEU_VolumeCache>();
							volumeCache.Initialize(ownerNode, 0);
							volumeCache.StartUpdateLayers();
						}
						else if (volumeCaches.Count == 1)
						{
							// Keep the single volumecache
							volumeCache = volumeCaches[0];
						}

						if (!updatedCaches.Contains(volumeCache))
						{
							updatedCaches.Add(volumeCache);
						}
					}
					else
					{
						// Reuse the updated cache
						volumeCache = updatedCaches[0];
					}

					volumeCache.UpdateLayerFromPart(session, volumeParts[i]);
				}
			}

			foreach (HEU_VolumeCache cache in updatedCaches)
			{
				// Add to parent for UI and preset
				parentAsset.AddVolumeCache(cache);

				// Finish update by keeping just the layers in use for each volume cache.
				cache.FinishUpdateLayers();
			}

			return updatedCaches;
		}

		public void Initialize(HEU_GeoNode ownerNode, int tileIndex)
		{
			_ownerNode = ownerNode;
			_geoName = ownerNode.GeoName;
			_objName = ownerNode.ObjectNode.ObjectName;
			_tileIndex = tileIndex;
			_terrainData = null;
		}

		public void ResetParameters()
		{
			_terrainData = null;

			HEU_VolumeLayer defaultLayer = new HEU_VolumeLayer();

			foreach (HEU_VolumeLayer layer in _layers)
			{
				CopyLayer(defaultLayer, layer);
			}
		}

		public HEU_VolumeLayer GetLayer(string layerName)
		{
			foreach(HEU_VolumeLayer layer in _layers)
			{
				if(layer._layerName.Equals(layerName))
				{
					return layer;
				}
			}
			return null;
		}

		public void StartUpdateLayers()
		{
			// Start with new layer list, as otherwise keeping existing layers
			// will cause removed layers (by user) to be kept around
			_updatedLayers = new List<HEU_VolumeLayer>();
		}

		public void FinishUpdateLayers()
		{
			_layers = _updatedLayers;
			_updatedLayers = null;
		}

		private void GetPartLayerAttributes(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_NodeId partID, HEU_VolumeLayer layer)
		{
			// Get the tile index, if it exists, for this part
			HAPI_AttributeInfo tileAttrInfo = new HAPI_AttributeInfo();
			int[] tileAttrData = new int[0];
			HEU_GeneralUtility.GetAttribute(session, geoID, partID, HEU_Defines.HAPI_HEIGHTFIELD_TILE_ATTR, ref tileAttrInfo, ref tileAttrData, session.GetAttributeIntData);
			if (tileAttrData != null && tileAttrData.Length > 0)
			{
				layer._tile = tileAttrData[0];
				//Debug.LogFormat("Tile: {0}", tileAttrData[0]);
			}
			else
			{
				layer._tile = 0;
			}


			string[] layerAttrNames =
			{
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_DIFFUSE_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_MASK_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_NORMAL_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_NORMAL_SCALE_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_METALLIC_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_SMOOTHNESS_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_SPECULAR_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TILE_OFFSET_ATTR,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TILE_SIZE_ATTR
			};

			// Check if any of the layer attribute names show up in the existing primitive attributes
			layer._hasLayerAttributes = false;
			HAPI_AttributeInfo attrInfo = new HAPI_AttributeInfo();
			bool bResult = false;
			foreach (string layerAttr in layerAttrNames)
			{
				bResult = session.GetAttributeInfo(geoID, partID, layerAttr, HAPI_AttributeOwner.HAPI_ATTROWNER_PRIM, ref attrInfo);
				if (bResult && attrInfo.exists)
				{
					layer._hasLayerAttributes = true;
					break;
				}
			}
		}

		private bool LoadLayerTextureFromAttribute(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_NodeId partID, string attrName, out Texture2D outTexture)
		{
			outTexture = null;
			// The texture path is stored as string primitive attribute. Only 1 string path per layer.
			HAPI_AttributeInfo attrInfo = new HAPI_AttributeInfo();
			string[] texturePath = HEU_GeneralUtility.GetAttributeStringData(session, geoID, partID, attrName, ref attrInfo);
			if (texturePath != null && texturePath.Length > 0 && !string.IsNullOrEmpty(texturePath[0]))
			{
				outTexture = LoadAssetTexture(texturePath[0]);
			}
			return outTexture != null;
		}

		private bool LoadLayerFloatFromAttribute(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_NodeId partID, string attrName, ref float floatValue)
		{
			HAPI_AttributeInfo attrInfo = new HAPI_AttributeInfo();
			float[] attrValues = new float[0];
			HEU_GeneralUtility.GetAttribute(session, geoID, partID, attrName, ref attrInfo, ref attrValues, session.GetAttributeFloatData);
			if (attrValues != null && attrValues.Length > 0)
			{
				floatValue = attrValues[0];
				return true;
			}
			return false;
		}

		private bool LoadLayerColorFromAttribute(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_NodeId partID, string attrName, ref Color colorValue)
		{
			HAPI_AttributeInfo attrInfo = new HAPI_AttributeInfo();
			float[] attrValues = new float[0];
			HEU_GeneralUtility.GetAttribute(session, geoID, partID, attrName, ref attrInfo, ref attrValues, session.GetAttributeFloatData);
			if (attrValues != null && attrValues.Length >= 3 && attrInfo.tupleSize >= 3)
			{
				colorValue[0] = attrValues[0];
				colorValue[1] = attrValues[1];
				colorValue[2] = attrValues[2];

				if (attrInfo.tupleSize == 4 && attrValues.Length == 4)
				{
					colorValue[3] = attrValues[3];
				}
				else
				{
					colorValue[3] = 1f;
				}
				return true;
			}
			return false;
		}

		private bool LoadLayerVector2FromAttribute(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_NodeId partID, string attrName, ref Vector2 vectorValue)
		{
			HAPI_AttributeInfo attrInfo = new HAPI_AttributeInfo();
			float[] attrValues = new float[0];
			HEU_GeneralUtility.GetAttribute(session, geoID, partID, attrName, ref attrInfo, ref attrValues, session.GetAttributeFloatData);
			if (attrValues != null && attrValues.Length == 2)
			{
				if (attrInfo.tupleSize == 2)
				{
					vectorValue[0] = attrValues[0];
					vectorValue[1] = attrValues[1];
					return true;
				}
			}
			return false;
		}

		public void UpdateLayerFromPart(HEU_SessionBase session, HEU_PartData part)
		{
			HEU_GeoNode geoNode = part.ParentGeoNode;

			HAPI_VolumeInfo volumeInfo = new HAPI_VolumeInfo();
			bool bResult = session.GetVolumeInfo(geoNode.GeoID, part.PartID, ref volumeInfo);
			if (!bResult || volumeInfo.tupleSize != 1 || volumeInfo.zLength != 1 || volumeInfo.storage != HAPI_StorageType.HAPI_STORAGETYPE_FLOAT)
			{
				return;
			}

			string volumeName = HEU_SessionManager.GetString(volumeInfo.nameSH, session);
			part.SetVolumeLayerName(volumeName);

			//Debug.LogFormat("Part name: {0}, GeoName: {1}, Volume Name: {2}, Display: {3}", part.PartName, geoNode.GeoName, volumeName, geoNode.Displayable);

			bool bHeightPart = volumeName.Equals(HEU_Defines.HAPI_HEIGHTFIELD_LAYERNAME_HEIGHT);
			bool bMaskPart = volumeName.Equals(HEU_Defines.HAPI_HEIGHTFIELD_LAYERNAME_MASK);

			HEU_VolumeLayer layer = GetLayer(volumeName);
			if (layer == null)
			{
				layer = new HEU_VolumeLayer();
				layer._layerName = volumeName;

				if (bHeightPart)
				{
					_layers.Insert(0, layer);
				}
				else if(!bMaskPart)
				{
					_layers.Add(layer);
				}
			}

			layer._part = part;

			if (!bMaskPart)
			{
				GetPartLayerAttributes(session, geoNode.GeoID, part.PartID, layer);
			}

			if (!bHeightPart)
			{
				part.DestroyAllData();
			}

			if (!_updatedLayers.Contains(layer))
			{
				if (bHeightPart)
				{
					_updatedLayers.Insert(0, layer);
				}
				else if (!bMaskPart)
				{
					_updatedLayers.Add(layer);
				}
			}
		}

		public void GenerateTerrainWithAlphamaps(HEU_SessionBase session, HEU_HoudiniAsset houdiniAsset, bool bRebuild)
		{
			if(_layers == null || _layers.Count == 0)
			{
				Debug.LogError("Unable to generate terrain due to lack of heightfield layers!");
				return;
			}

			HEU_VolumeLayer heightLayer = _layers[0];

			HAPI_VolumeInfo heightVolumeInfo = new HAPI_VolumeInfo();
			bool bResult = session.GetVolumeInfo(_ownerNode.GeoID, heightLayer._part.PartID, ref heightVolumeInfo);
			if (!bResult)
			{
				Debug.LogErrorFormat("Unable to get volume info for height layer: {0}!", heightLayer._layerName);
				return;
			}

			// Special handling of volume cache presets. It is applied here (if exists) because it might pertain to TerrainData that exists
			// in the AssetDatabase. If we don't apply here but rather create a new one, the existing file will get overwritten.
			// Applying the preset here for terrain ensures the TerrainData is reused.
			// Get the volume preset for this part
			HEU_VolumeCachePreset volumeCachePreset = houdiniAsset.GetVolumeCachePreset(_ownerNode.ObjectNode.ObjectName, _ownerNode.GeoName, TileIndex);
			if (volumeCachePreset != null)
			{
				ApplyPreset(volumeCachePreset);

				// Remove it so that it doesn't get applied when doing the recook step
				houdiniAsset.RemoveVolumeCachePreset(volumeCachePreset);
			}

			// The TerrainData and TerrainLayer files needs to be saved out if we create them. This creates the relative folder
			// path from the Asset's cache folder: {assetCache}/{geo name}/Terrain/Tile{tileIndex}/...
			string relativeFolderPath = HEU_Platform.BuildPath(_ownerNode.GeoName, HEU_Defines.HEU_FOLDER_TERRAIN, HEU_Defines.HEU_FOLDER_TILE + TileIndex);

			if (bRebuild)
			{
				// For full rebuild, re-create the TerrainData instead of using previous
				_terrainData = null;
			}

			//Debug.Log("Generating Terrain with AlphaMaps: " + (_terrainData != null ? _terrainData.name : "NONE"));
			TerrainData terrainData = _terrainData;
			Vector3 terrainOffsetPosition = Vector3.zero;

			// Look up TerrainData file via attribute if user has set it
			string terrainDataFile = HEU_GeneralUtility.GetAttributeStringValueSingle(session, _ownerNode.GeoID, heightLayer._part.PartID,
				HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TERRAINDATA_FILE_ATTR, HAPI_AttributeOwner.HAPI_ATTROWNER_PRIM);
			if (!string.IsNullOrEmpty(terrainDataFile))
			{
				TerrainData loadedTerrainData = HEU_AssetDatabase.LoadAssetAtPath(terrainDataFile, typeof(TerrainData)) as TerrainData;
				if (loadedTerrainData == null)
				{
					Debug.LogWarningFormat("TerrainData, set via attribute, not found at: {0}", terrainDataFile);
				}
				else
				{
					// In the case that the specified TerrainData belongs to another Terrain (i.e. input Terrain), 
					// make a copy of it and store it in our cache. Note that this overwrites existing TerrainData in our cache
					// because the workflow is such that attributes will always override local setting.
					string bakedTerrainPath = houdiniAsset.GetValidAssetCacheFolderPath();
					bakedTerrainPath = HEU_Platform.BuildPath(bakedTerrainPath, relativeFolderPath);
					terrainData = HEU_AssetDatabase.CopyAndLoadAssetAtAnyPath(loadedTerrainData, bakedTerrainPath, typeof(TerrainData), true) as TerrainData;
					if (terrainData == null)
					{
						Debug.LogErrorFormat("Unable to copy TerrainData from {0} for generating Terrain.", terrainDataFile);
					}
				}
			}

			// Generate the terrain and terrain data from the height layer. This applies height values.
			bResult = HEU_GeometryUtility.GenerateTerrainFromVolume(session, ref heightVolumeInfo, heightLayer._part.ParentGeoNode.GeoID,
				heightLayer._part.PartID, heightLayer._part.OutputGameObject, ref terrainData, out terrainOffsetPosition);
			if (!bResult || terrainData == null)
			{
				return;
			}

			if (_terrainData != terrainData)
			{
				_terrainData = terrainData;
				heightLayer._part.SetTerrainData(terrainData, relativeFolderPath);
			}

			heightLayer._part.SetTerrainOffsetPosition(terrainOffsetPosition);

			int terrainSize = terrainData.heightmapResolution;

			// Now process TerrainLayers and alpha maps

			// First, preprocess all layers to get heightfield arrays, converted to proper size
			List<float[]> heightFields = new List<float[]>();
			// Corresponding list of HF volume layers to process as splatmaps
			List<HEU_VolumeLayer> volumeLayersToProcess = new List<HEU_VolumeLayer>();

			int numLayers = _layers.Count;
			float minHeight = 0;
			float maxHeight = 0;
			float  heightRange = 0;
			// This skips the height layer, and processes all other layers.
			// Note that mask shouldn't be part of _layers at this point.
			for(int i = 1; i < numLayers; ++i)
			{
				float[] normalizedHF = HEU_GeometryUtility.GetNormalizedHeightmapFromPartWithMinMax(session, _ownerNode.GeoID, _layers[i]._part.PartID, terrainSize,
					ref minHeight, ref maxHeight, ref heightRange);
				if (normalizedHF != null && normalizedHF.Length > 0)
				{
					heightFields.Add(normalizedHF);
					volumeLayersToProcess.Add(_layers[i]);
				}
			}

			int numVolumeLayers = volumeLayersToProcess.Count;

			HAPI_NodeId geoID;
			HAPI_PartId partID;

			Texture2D defaultTexture = LoadDefaultSplatTexture();

#if UNITY_2018_3_OR_NEWER

			// Create or update the terrain layers based on heightfield layers.

			// Keep existing TerrainLayers, and either update or append to them
			TerrainLayer[] existingTerrainLayers = terrainData.terrainLayers;

			// Total layers are existing layers + new alpha maps
			List<TerrainLayer> finalTerrainLayers = new List<TerrainLayer>(existingTerrainLayers);

			// This holds the alpha map indices for each layer that will be added to the TerrainData.
			// The alpha maps could be a mix of existing and new values, so need to know which to use
			// Initially set to use existing alpha maps, then override later on if specified via HF layers
			List<int> alphaMapIndices = new List<int>();
			for (int a = 0; a < existingTerrainLayers.Length; ++a)
			{
				// Negative indices for existing alpha map (offset by -1)
				alphaMapIndices.Add(-a - 1);
			}

			bool bNewTerrainLayer = false;
			HEU_VolumeLayer layer = null;
			TerrainLayer terrainLayer = null;
			bool bSetTerrainLayerProperties = true;
			for (int m = 0; m < numVolumeLayers; ++m)
			{
				bNewTerrainLayer = false;
				bSetTerrainLayerProperties = true;

				layer = volumeLayersToProcess[m];

				geoID = _ownerNode.GeoID;
				partID = layer._part.PartID;

				// Try to find existing TerrainLayer by name.
				terrainLayer = null;
				int terrainLayerIndex = GetTerrainLayerIndexByName(layer._layerName, existingTerrainLayers);
				if (terrainLayerIndex >= 0)
				{
					// Note the terrainLayerIndex is same for finalTerrainLayers
					terrainLayer = existingTerrainLayers[terrainLayerIndex];

					// Positive index for alpha map from heightfield (starting at 1)
					alphaMapIndices[terrainLayerIndex] = m + 1;
				}

				if (terrainLayer == null)
				{
					// Not found, so look for TerrainLayer attribute (file path) or create one

					string terrainLayerFile = HEU_GeneralUtility.GetAttributeStringValueSingle(session, geoID, partID,
						HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TERRAINLAYER_FILE_ATTR, HAPI_AttributeOwner.HAPI_ATTROWNER_PRIM);
					if (!string.IsNullOrEmpty(terrainLayerFile))
					{
						terrainLayer = HEU_AssetDatabase.LoadAssetAtPath(terrainLayerFile, typeof(TerrainLayer)) as TerrainLayer;
						if (terrainLayer == null)
						{
							Debug.LogWarningFormat("TerrainLayer, set via attribute, not found at: {0}", terrainLayerFile);
							// Not earlying out or skipping this layer due to error because we want to keep proper indexing
							// by creating a new TerrainLayer.
						}
						else
						{
							// Now check existing TerrainLayers again to see if we have this layer.
							// This check is required because Unity uses the file name for layer name, which means
							// the user could have specified a layer name different from the TerrainLayer file name.
							terrainLayerIndex = GetTerrainLayerIndexByName(terrainLayer.name, existingTerrainLayers);
							if (terrainLayerIndex >= 0)
							{
								// Note the terrainLayerIndex is same for finalTerrainLayers
								terrainLayer = existingTerrainLayers[terrainLayerIndex];

								// Positive index for alpha map from heightfield (starting at 1)
								alphaMapIndices[terrainLayerIndex] = m + 1;
							}
						}
					}

					// Still not found, so just create a new one
					if (terrainLayer == null)
					{
						terrainLayer = new TerrainLayer();
						terrainLayer.name = layer._layerName;
						//Debug.LogFormat("Created new TerrainLayer with name: {0} ", terrainLayer.name);
						bNewTerrainLayer = true;
					}

					if (terrainLayerIndex == -1)
					{
						// Adding to the finalTerrainLayers if this is indeed a newly created or loaded TerrainLayer
						// (i.e. isn't already part of the TerrainLayers for this Terrain).
						// Save this layer's index for later on if we make a copy.
						terrainLayerIndex = finalTerrainLayers.Count;
						finalTerrainLayers.Add(terrainLayer);

						// Positive index for alpha map from heightfield (starting at 1)
						alphaMapIndices.Add(m + 1);
					}
				}

				// For existing TerrainLayer, make a copy of it if it has custom layer attributes
				// because we don't want to change the original TerrainLayer.
				if (!bNewTerrainLayer && layer._hasLayerAttributes)
				{
					string bakedTerrainPath = houdiniAsset.GetValidAssetCacheFolderPath();
					bakedTerrainPath = HEU_Platform.BuildPath(bakedTerrainPath, relativeFolderPath);
					TerrainLayer prevTerrainLayer = terrainLayer;
					terrainLayer = HEU_AssetDatabase.CopyAndLoadAssetAtAnyPath(terrainLayer, bakedTerrainPath, typeof(TerrainLayer), true) as TerrainLayer;
					if (terrainLayer != null)
					{
						// Update the TerrainLayer reference in the list with this copy
						finalTerrainLayers[terrainLayerIndex] = terrainLayer;
					}
					else
					{
						Debug.LogErrorFormat("Unable to copy TerrainLayer '{0}' for generating Terrain. "
							+ "Using original TerrainLayer. Will not be able to set any TerrainLayer properties.", layer._layerName);
						terrainLayer = prevTerrainLayer;
						bSetTerrainLayerProperties = false;
						// Again, continuing on to keep proper indexing.
					}
				}

				// Now override layer properties if they have been set via attributes
				if (bSetTerrainLayerProperties)
				{
					LoadLayerPropertiesFromAttributes(session, geoID, partID, terrainLayer, bNewTerrainLayer, defaultTexture);
				}

				if (bNewTerrainLayer)
				{
					// In order to retain the new TerrainLayer, it must be saved to the AssetDatabase.
					Object savedObject = null;
					string layerFileNameWithExt = terrainLayer.name;
					if (!layerFileNameWithExt.EndsWith(HEU_Defines.HEU_EXT_TERRAINLAYER))
					{
						layerFileNameWithExt += HEU_Defines.HEU_EXT_TERRAINLAYER;
					}
					houdiniAsset.AddToAssetDBCache(layerFileNameWithExt, terrainLayer, relativeFolderPath, ref savedObject);
				}
			}

			// Get existing alpha maps so we can reuse the values if needed
			float[,,] existingAlphaMaps = terrainData.GetAlphamaps(0, 0, terrainSize, terrainSize);

			terrainData.terrainLayers = finalTerrainLayers.ToArray();

			int numTotalAlphaMaps = finalTerrainLayers.Count;

#else
			// Create or update the SplatPrototype based on heightfield layers.

			// Need to create or reuse SplatPrototype for each layer in heightfield, representing the textures.
			SplatPrototype[] existingSplats = terrainData.splatPrototypes;

			// A full rebuild clears out existing splats, but a regular cook keeps them.
			List<SplatPrototype> finalSplats = new List<SplatPrototype>(existingSplats);

			// This holds the alpha map indices for each layer that will be added to the TerrainData
			// The alpha maps could be a mix of existing and new values, so need to know which to use
			List<int> alphaMapIndices = new List<int>();

			// Initially set to use existing alpha maps, then override later on if specified via HF layers.
			for (int a = 0; a < existingSplats.Length; ++a)
			{
				// Negative indices for existing alpha map (offset by -1)
				alphaMapIndices.Add(-a - 1);
			}

			bool bNewSplat = false;
			HEU_VolumeLayer layer = null;
			SplatPrototype splatPrototype = null;

			for (int m = 0; m < numVolumeLayers; ++m)
			{
				bNewSplat = false;

				layer = volumeLayersToProcess[m];

				geoID = _ownerNode.GeoID;
				partID = layer._part.PartID;

				// Try to find existing SplatPrototype for reuse. But not for full rebuild.
				splatPrototype = null;
				if (layer._splatPrototypeIndex >= 0 && layer._splatPrototypeIndex < existingSplats.Length)
				{
					splatPrototype = existingSplats[layer._splatPrototypeIndex];

					// Positive index for alpha map from heightfield (starting at 1)
					alphaMapIndices[layer._splatPrototypeIndex] = m + 1;
				}

				if (splatPrototype == null)
				{
					splatPrototype = new SplatPrototype();
					layer._splatPrototypeIndex = finalSplats.Count;
					finalSplats.Add(splatPrototype);

					// Positive index for alpha map from heightfield (starting at 1)
					alphaMapIndices.Add(m + 1);
				}

				// Now override splat properties if they have been set via attributes
				LoadLayerPropertiesFromAttributes(session, geoID, partID, splatPrototype, bNewSplat, defaultTexture);
			}

			// On regular cook, get existing alpha maps so we can reuse the values if needed.
			float[,,] existingAlphaMaps = terrainData.GetAlphamaps(0, 0, terrainSize, terrainSize);

			terrainData.splatPrototypes = finalSplats.ToArray();

			int numTotalAlphaMaps = finalSplats.Count;
#endif

			// Set alpha maps by combining with existing alpha maps, and appending new heightfields

			float[,,] alphamap = null;
			if (numTotalAlphaMaps > 0)
			{
				// Convert the heightfields into alpha maps with layer strengths
				float[] strengths = new float[volumeLayersToProcess.Count];
				for (int m = 0; m < volumeLayersToProcess.Count; ++m)
				{
					strengths[m] = volumeLayersToProcess[m]._strength;
				}

				alphamap = HEU_GeometryUtility.AppendConvertedHeightFieldToAlphaMap(terrainSize, existingAlphaMaps, heightFields, strengths, alphaMapIndices);

				terrainData.SetAlphamaps(0, 0, alphamap);
			}

			// If the layers were writen out, this saves the asset DB. Otherwise user has to save it themselves.
			// Not 100% sure this is needed, but without this the editor doesn't know the terrain asset has been updated
			// and therefore doesn't import and show the terrain layer.
			HEU_AssetDatabase.SaveAssetDatabase();
		}

#if UNITY_2018_3_OR_NEWER
		public void LoadLayerPropertiesFromAttributes(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_PartId partID, TerrainLayer terrainLayer,
			bool bNewTerrainLayer, Texture2D defaultTexture)
		{
			Texture2D diffuseTexture = null;
			if (LoadLayerTextureFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_DIFFUSE_ATTR, out diffuseTexture))
			{
				terrainLayer.diffuseTexture = diffuseTexture;
			}

			if (terrainLayer.diffuseTexture == null && bNewTerrainLayer)
			{
				// Applying default texture if this layer was created newly and no texture was specified.
				// Unity always seems to require a default texture when creating a new layer normally.
				terrainLayer.diffuseTexture = defaultTexture;
			}

			Texture2D maskTexture = null;
			if (LoadLayerTextureFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_MASK_ATTR, out maskTexture))
			{
				terrainLayer.maskMapTexture = maskTexture;
			}

			Texture2D normalTexture = null;
			if (LoadLayerTextureFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_NORMAL_ATTR, out normalTexture))
			{
				terrainLayer.normalMapTexture = normalTexture;
			}

			float normalScale = 0f;
			if (LoadLayerFloatFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_NORMAL_SCALE_ATTR, ref normalScale))
			{
				terrainLayer.normalScale = normalScale;
			}

			float metallic = 0f;
			if (LoadLayerFloatFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_METALLIC_ATTR, ref metallic))
			{
				terrainLayer.metallic = metallic;
			}

			float smoothness = 0f;
			if (LoadLayerFloatFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_SMOOTHNESS_ATTR, ref smoothness))
			{
				terrainLayer.smoothness = smoothness;
			}

			Color specularColor = new Color();
			if (LoadLayerColorFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_SPECULAR_ATTR, ref specularColor))
			{
				terrainLayer.specular = specularColor;
			}

			Vector2 tileOffset = new Vector2();
			if (LoadLayerVector2FromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TILE_OFFSET_ATTR, ref tileOffset))
			{
				terrainLayer.tileOffset = tileOffset;
			}

			Vector2 tileSize = new Vector2();
			if (LoadLayerVector2FromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TILE_SIZE_ATTR, ref tileSize))
			{
				terrainLayer.tileSize = tileSize;
			}

			if (terrainLayer.tileSize.magnitude == 0f)
			{
				// Use texture size if tile size is 0
				terrainLayer.tileSize = new Vector2(terrainLayer.diffuseTexture.width, terrainLayer.diffuseTexture.height);
			}
		}
#else
	public void LoadLayerPropertiesFromAttributes(HEU_SessionBase session, HAPI_NodeId geoID, HAPI_PartId partID, SplatPrototype splat,
			bool bNewTerrainLayer, Texture2D defaultTexture)
		{
			Texture2D diffuseTexture = null;
			if (LoadLayerTextureFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_DIFFUSE_ATTR, out diffuseTexture))
			{
				splat.texture = diffuseTexture;
			}

			if (splat.texture == null && bNewTerrainLayer)
			{
				// Applying default texture if this layer was created newly and no texture was specified.
				// Unity always seems to require a default texture when creating a new layer normally.
				splat.texture = defaultTexture;
			}

			if (splat.texture == null)
			{
				splat.texture = defaultTexture;
			}

			Texture2D normalTexture = null;
			if (LoadLayerTextureFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TEXTURE_NORMAL_ATTR, out normalTexture))
			{
				splat.normalMap = normalTexture;
			}

			float metallic = 0f;
			if (LoadLayerFloatFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_METALLIC_ATTR, ref metallic))
			{
				splat.metallic = metallic;
			}

			float smoothness = 0f;
			if (LoadLayerFloatFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_SMOOTHNESS_ATTR, ref smoothness))
			{
				splat.smoothness = smoothness;
			}

			Color specularColor = new Color();
			if (LoadLayerColorFromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_SPECULAR_ATTR, ref specularColor))
			{
				splat.specular = specularColor;
			}

			Vector2 tileOffset = new Vector2();
			if (LoadLayerVector2FromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TILE_OFFSET_ATTR, ref tileOffset))
			{
				splat.tileOffset = tileOffset;
			}

			Vector2 tileSize = new Vector2();
			if (LoadLayerVector2FromAttribute(session, geoID, partID, HEU_Defines.DEFAULT_UNITY_HEIGHTFIELD_TILE_SIZE_ATTR, ref tileSize))
			{
				splat.tileSize = tileSize;
			}

			if (splat.tileSize.magnitude == 0f)
			{
				// Use texture size if tile size is 0
				splat.tileSize = new Vector2(splat.texture.width, splat.texture.height);
			}
		}
#endif

	public void PopulatePreset(HEU_VolumeCachePreset cachePreset)
		{
			cachePreset._objName = ObjectName;
			cachePreset._geoName = GeoName;
			cachePreset._uiExpanded = UIExpanded;
			cachePreset._tile = TileIndex;

			if (_terrainData != null)
			{
				cachePreset._terrainDataPath = HEU_AssetDatabase.GetAssetPath(_terrainData);
			}
			else
			{
				cachePreset._terrainDataPath = "";
			}
			//Debug.Log("Set terraindata path: " + cachePreset._terrainDataPath);

			foreach (HEU_VolumeLayer layer in _layers)
			{
				HEU_VolumeLayerPreset layerPreset = new HEU_VolumeLayerPreset();

				layerPreset._layerName = layer._layerName;
				layerPreset._strength = layer._strength;
				layerPreset._uiExpanded = layer._uiExpanded;
				layerPreset._tile = layer._tile;

				cachePreset._volumeLayersPresets.Add(layerPreset);
			}
		}

		public bool ApplyPreset(HEU_VolumeCachePreset volumeCachePreset)
		{
			UIExpanded = volumeCachePreset._uiExpanded;

			// Load the TerrainData if the path is given
			//Debug.Log("Get terraindata path: " + volumeCachePreset._terrainDataPath);
			if (!string.IsNullOrEmpty(volumeCachePreset._terrainDataPath))
			{
				_terrainData = HEU_AssetDatabase.LoadAssetAtPath(volumeCachePreset._terrainDataPath, typeof(TerrainData)) as TerrainData;
				//Debug.Log("Loaded terrain? " + (_terrainData != null ? "yes" : "no"));
			}

			foreach (HEU_VolumeLayerPreset layerPreset in volumeCachePreset._volumeLayersPresets)
			{
				HEU_VolumeLayer layer = GetLayer(layerPreset._layerName);
				if (layer == null)
				{
					Debug.LogWarningFormat("Volume layer with name {0} not found! Unable to set heightfield layer preset.", layerPreset._layerName);
					return false;
				}

				layer._strength = layerPreset._strength;
				layer._tile = layerPreset._tile;
				layer._uiExpanded = layerPreset._uiExpanded;
			}
			
			IsDirty = true;

			return true;
		}

		public void CopyValuesTo(HEU_VolumeCache destCache)
		{
			destCache.UIExpanded = UIExpanded;

			destCache._terrainData = Object.Instantiate(_terrainData);

			foreach (HEU_VolumeLayer srcLayer in _layers)
			{
				HEU_VolumeLayer destLayer = destCache.GetLayer(srcLayer._layerName);
				if(destLayer != null)
				{
					CopyLayer(srcLayer, destLayer);
				}
			}
		}

		public static void CopyLayer(HEU_VolumeLayer srcLayer, HEU_VolumeLayer destLayer)
		{
			destLayer._strength = srcLayer._strength;
			destLayer._uiExpanded = srcLayer._uiExpanded;
			destLayer._tile = srcLayer._tile;
		}

		public static Texture2D LoadDefaultSplatTexture()
		{
			Texture2D texture = LoadAssetTexture(HEU_PluginSettings.TerrainSplatTextureDefault);
			if (texture == null)
			{
				texture = HEU_MaterialFactory.WhiteTexture();
			}
			return texture;
		}

		public static Texture2D LoadAssetTexture(string path)
		{
			Texture2D texture = HEU_MaterialFactory.LoadTexture(path);
			if (texture == null)
			{
				Debug.LogErrorFormat("Unable to find the default Terrain texture at {0}. Make sure this default texture exists.", path);
			}
			return texture;
		}

#if UNITY_2018_3_OR_NEWER
		private static int GetTerrainLayerIndexByName(string layerName, TerrainLayer[] terrainLayers)
		{
			string layerFileName = layerName;
			string layerFileNameWithSpaces = layerName.Replace('_', ' ');
			for (int i = 0; i < terrainLayers.Length; ++i)
			{
				if (terrainLayers[i] != null && terrainLayers[i].name != null 
					&& (terrainLayers[i].name.Equals(layerFileName, System.StringComparison.CurrentCultureIgnoreCase) 
					|| terrainLayers[i].name.Equals(layerFileNameWithSpaces, System.StringComparison.CurrentCultureIgnoreCase)))
				{
					return i;
				}
			}
			return -1;
		}
#endif
	}

}   // HoudiniEngineUnity