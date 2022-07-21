using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Assertions;

namespace Netcode.Extensions.NetObjPool2
{
    /// <summary>
    /// Object Pool for networked objects, used for controlling how objects are spawned by Netcode. Netcode by default will allocate new memory when spawning new
    /// objects. With this Networked Pool, we're using custom spawning to reuse objects.
    /// Hooks to NetworkManager's prefab handler to intercept object spawning and do custom actions.
    ///
    /// This differs from the other implementation by adding in some functionality to call actions when the pool has been initialized/destroyed,
    /// and identifying 
    /// </summary>
    public class NetworkObjectPool2 : NetworkBehaviour
    {

        private static NetworkObjectPool2 _instance;

        public static NetworkObjectPool2 Singleton => _instance;

        [SerializeField] private List<PoolConfigObject2> pooledPrefabsList;

        private readonly HashSet<GameObject> _prefabs = new HashSet<GameObject>();

        private readonly IDictionary<string, GameObject> _namedPrefabs = new Dictionary<string, GameObject>();

        private readonly Dictionary<GameObject, Queue<NetworkObject>> _pooledObjects = new Dictionary<GameObject, Queue<NetworkObject>>();

        private readonly List<string> _prefabNames = new List<string>();

        public IReadOnlyList<string> PrefabNames => _prefabNames.AsReadOnly();

        private bool _hasInitialized = false;

        public bool HasInitialized => _hasInitialized;

        /// <summary>Action that will be invoked upon the NetworkObjectPool2 Singleton being ready.</summary>
        private static Action _onPoolInitializedAction;

        //private static Action<string, NetworkObjectReference> _onPrefabWithGivenIdentifierInstantiated;

        /// <summary>Action that will be invoked upon the NetworkObjectPool2 Singleton being destroyed.</summary>
        private static Action _onDestroySingletonOneShot;

        public void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
            }
            else
            {
                _instance = this;
            }
        }

        public override void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                _onDestroySingletonOneShot?.Invoke();
                _onDestroySingletonOneShot = null;
            }
            
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            InitializePool();
        }

        public override void OnNetworkDespawn()
        {
            ClearPool();
        }

        

        /// <summary>
        /// Pass an action to the pool, to be done when it's initialized. If it's already been initialized, don't do the thing.
        /// </summary>
        /// <param name="doThis">Action to perform when the pool has been initialized.</param>
        /// <returns>True if the action has been queued to be done on initialization.
        /// False if the pool is already initialized (can't queue this action) or if not the server.</returns>
        public bool DoThisWhenInititalized(Action doThis)
        {
            if (!NetworkManager.Singleton.IsServer) return false;

            if (_hasInitialized)
            {
                return false;
            }
            _onPoolInitializedAction += doThis;
            return true;
        }

        /// <summary>
        /// Pass an action to the NetworkObjectPool2. If it's initialized already, we do that action now.
        /// If it's not yet initialized, we do that action as soon as this has been initialized.
        /// Returns true if action has been done/queued, returns false if you're passing this from a client.
        /// </summary>
        /// <param name="doThis">Action to perform now or as soon as initialization is done.</param>
        /// <returns>true if the action has been queued/done (due to being server), false otherwise</returns>
        public bool DoThisNowOrAsSoonAsInitializationIsDone(Action doThis)
        {
            if (!NetworkManager.Singleton.IsServer) return false;
            
            
            
            if (_hasInitialized)
            {
                doThis.Invoke();
            }
            else
            {
                _onPoolInitializedAction += doThis;
            }
            return true;
        }


        /// <summary>
        /// Does the same sort of thing as <see cref="DoThisNowOrAsSoonAsInitializationIsDone"/>
        /// </summary>
        /// <param name="doThis">Action to be performed as soon as the singleton is ready (or right now)</param>
        /// <returns>False if not server (action not queued/performed). True if server (action was queued for when the singleton is ready/was done)</returns>
        public static bool DoThisAsSoonAsSingletonIsReady(Action doThis)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                return false;
            }
            if (Singleton != null && Singleton._hasInitialized)
            {
                doThis.Invoke();
            }
            else
            {
                _onPoolInitializedAction += doThis;
            }
            return true;
        }

        public void OnValidate()
        {
            
            List<string> usedNames = new List<string>();
            
            for (var i = 0; i < pooledPrefabsList.Count; i++)
            {
                var prefab = pooledPrefabsList[i].prefab;
                if (prefab != null)
                {
                    Assert.IsNotNull(
                        prefab.GetComponent<NetworkObject>(),
                        $"{nameof(NetworkObjectPool2)}: Pooled prefab \"{prefab.name}\" at index {i.ToString()} has no {nameof(NetworkObject)} component.");
                    
                }
                
                var prewarmCount = PooledPrefabsList[i].prewarmCount;
                if (prewarmCount < 0)
                {
                    Debug.LogWarning($"{nameof(NetworkObjectPool2)}: Pooled prefab at index {i.ToString()} has a negative prewarm count! Making it not negative.");
                    var thisPooledPrefab = PooledPrefabsList[i];
                    thisPooledPrefab.PrewarmCount *= -1;
                    PooledPrefabsList[i] = thisPooledPrefab;
                }
                

                if (string.IsNullOrWhiteSpace(pooledPrefabsList[i].poolID))
                {
                    var currentPooledPrefab = pooledPrefabsList[i];
                    currentPooledPrefab.poolID = currentPooledPrefab.prefab.name;
                    pooledPrefabsList[i] = currentPooledPrefab;
                }

                if (usedNames.Contains(pooledPrefabsList[i].poolID))
                {

                    var thisPooledPrefab = pooledPrefabsList[i];

                    var rawName = thisPooledPrefab.poolID;
                    
                    UnityEngine.Debug.LogError("Cannot have two prefabs with same DI, renaming duplicate!");
                    
                    var newName = rawName;
                    
                    for(var dupeNumber = 1; usedNames.Contains(newName); newName = $"{rawName} ({dupeNumber++})") {}

                    thisPooledPrefab.poolID = newName;
                    pooledPrefabsList[i] = thisPooledPrefab;
                }
                
                usedNames.Add(pooledPrefabsList[i].poolID);
            }
        }

        /// <summary>
        /// Gets an instance of the given prefab from the pool. The prefab must be registered to the pool.
        /// </summary>
        /// <param name="prefab"></param>
        /// <returns></returns>
        public NetworkObject GetNetworkObject(GameObject prefab)
        {
            return GetNetworkObjectInternal(prefab, Vector3.zero, Quaternion.identity);
        }


        /// <summary>
        /// Gets an instance of the given prefab from the pool. The prefab must be registered to the pool.
        /// </summary>
        /// <param name="prefab"></param>
        /// <param name="position">The position to spawn the object at.</param>
        /// <param name="rotation">The rotation to spawn the object with.</param>
        /// <returns></returns>
        public NetworkObject GetNetworkObject(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            return GetNetworkObjectInternal(prefab, position, rotation);
        }


        /// <summary>
        /// Gets an instance of the given prefab from the pool.
        /// The prefab must be registered to the pool.
        /// Based on the registered poolId string of the object in the pool.
        /// </summary>
        /// <param name="prefabName">The poolID name of the desired prefab</param>
        /// <returns></returns>
        public NetworkObject GetNamedNetworkObject(string prefabName)
        {
            return GetNamedNetworkObject(prefabName, Vector3.zero, Quaternion.identity);
        }

        /// <summary>
        /// Gets an instance of the given prefab from the pool. The prefab must be registered to the pool.
        //// Based on the registered poolId string of the object in the pool.
        /// </summary>
        /// <param name="prefabName">The poolID name of the desired prefab</param>
        /// <param name="position">The position to spawn the object at.</param>
        /// <param name="rotation">The rotation to spawn the object with.</param>
        /// <returns></returns>
        public NetworkObject GetNamedNetworkObject(string prefabName, Vector3 position, Quaternion rotation)
        {
            return GetNetworkObjectInternal(_namedPrefabs[prefabName], position, rotation);
        }

        /// <summary>
        /// Return an object to the pool (reset objects before returning).
        /// </summary>
        public void ReturnNetworkObject(NetworkObject networkObject, GameObject prefab)
        {
            if (networkObject.IsSpawned){networkObject.RemoveOwnership();}
            var go = networkObject.gameObject;
            go.SetActive(false);
            _pooledObjects[prefab].Enqueue(networkObject);
        }

        /// <summary>
        /// Adds a prefab to the list of spawnable prefabs.
        /// </summary>
        /// <param name="prefab">The prefab to add.</param>
        /// <param name="prewarmCount"></param>
        /// <param name="prefabName">an identifier for this prefab</param>
        /// <param name="prefabType">Classifier for this prefab</param>
        public void AddPrefab(GameObject prefab, int prewarmCount = 0, string prefabName = "")
        {
            var networkObject = prefab.GetComponent<NetworkObject>();

            if (prefabName == "") { prefabName = prefab.name;}
            
            

            Assert.IsNotNull(networkObject, $"{nameof(prefab)} must have {nameof(networkObject)} component.");
            Assert.IsFalse(_prefabs.Contains(prefab), $"Prefab {prefab.name} is already registered in the pool.");
            
            Assert.IsFalse(_namedPrefabs.ContainsKey(prefabName), $"A prefab with the identifier {prefabName} is already registered.");
            

            RegisterPrefabInternal(prefab, prewarmCount, prefabName);
        }

        /// <summary>
        /// Builds up the cache for a prefab.
        /// </summary>
        private void RegisterPrefabInternal(GameObject prefab, int prewarmCount, string prefabName)
        {
            _prefabs.Add(prefab);
            _namedPrefabs[prefabName] = prefab;
            _prefabNames.Add(prefabName);

            var prefabQueue = new Queue<NetworkObject>();
            _pooledObjects[prefab] = prefabQueue;
            for (int i = 0; i < prewarmCount; i++)
            {
                var go = CreateInstance(prefab);
                ReturnNetworkObject(go.GetComponent<NetworkObject>(), prefab);
            }

            // Register Netcode Spawn handlers
            NetworkManager.Singleton.PrefabHandler.AddHandler(prefab, new PooledPrefabInstanceHandler2(prefab, this));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private GameObject CreateInstance(GameObject prefab)
        {
            return Instantiate(prefab);
        }

        /// <summary>
        /// This matches the signature of <see cref="NetworkSpawnManager.SpawnHandlerDelegate"/>
        /// </summary>
        /// <param name="prefab"></param>
        /// <param name="position"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        private NetworkObject GetNetworkObjectInternal(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var queue = _pooledObjects[prefab];

            NetworkObject networkObject;
            if (queue.Count > 0)
            {
                networkObject = queue.Dequeue();
            }
            else
            {
                networkObject = CreateInstance(prefab).GetComponent<NetworkObject>();
            }
            

            // Here we must reverse the logic in ReturnNetworkObject.
            var go = networkObject.gameObject;
            
            go.SetActive(true);

            go.transform.position = position;
            go.transform.rotation = rotation;
            
            return networkObject;
        }

        /// <summary>
        /// Registers all objects in <see cref="pooledPrefabsList"/> to the cache.
        /// Invokes the _OnPoolInitializedAction (and also makes that action null after invoking it)
        /// </summary>
        public void InitializePool()
        {
            if (_hasInitialized) return;
            foreach (var configObject in pooledPrefabsList)
            {
                RegisterPrefabInternal(configObject.prefab, configObject.prewarmCount, configObject.poolID);
            }
            _hasInitialized = true;
            
            _onPoolInitializedAction?.Invoke();
            
            _onPoolInitializedAction = null;
        }

        /// <summary>
        /// Unregisters all objects in <see cref="pooledPrefabsList"/> from the cache.
        /// </summary>
        public void ClearPool()
        {
            foreach (var prefab in _prefabs)
            {
                // Unregister Netcode Spawn handlers
                NetworkManager.Singleton.PrefabHandler.RemoveHandler(prefab);
            }
            _pooledObjects.Clear();
        }
    }



    [Serializable]
    internal struct PoolConfigObject2
    {
        /// <summary>
        /// The prefab that is in the pool
        /// </summary>
        [FormerlySerializedAs("Prefab")] public GameObject prefab;
        /// <summary>
        /// How many prefabs of this type should be pre-initialized?
        /// </summary>
        [FormerlySerializedAs("PrewarmCount")] public int prewarmCount;
        /// <summary>
        /// A string identifier for the pooled prefab
        /// </summary>
        public string poolID;
    }
    
    

    internal class PooledPrefabInstanceHandler2 : INetworkPrefabInstanceHandler
    {
        private GameObject m_Prefab;
        private NetworkObjectPool2 m_Pool;

        public PooledPrefabInstanceHandler2(GameObject prefab, NetworkObjectPool2 pool)
        {
            m_Prefab = prefab;
            m_Pool = pool;
        }

        NetworkObject INetworkPrefabInstanceHandler.Instantiate(ulong ownerClientId, Vector3 position, Quaternion rotation)
        {
            var netObject = m_Pool.GetNetworkObject(m_Prefab, position, rotation);
            return netObject;
        }

        void INetworkPrefabInstanceHandler.Destroy(NetworkObject networkObject)
        {
            m_Pool.ReturnNetworkObject(networkObject, m_Prefab);
        }
    }

}