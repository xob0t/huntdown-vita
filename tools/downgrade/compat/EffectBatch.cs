using System.Collections.Generic;
using UnityEngine;

// The game's effects, debris and weather drawn as a few meshes instead of a SpriteRenderer each. On the PS Vita the
// engine spends about 20 us of the main thread on each visible sprite whatever it is (culling, sorting, batching:
// measured with compat/SpriteBench.cs), and in a fight most sprites on screen are these: a big explosion is some 17
// Effect sprites (Effects.cs: explosions, smoke, sparks, muzzle and hit flashes), and its debris, blood, shells,
// bullet holes and the weather (snow, rain, ash) are SpriteObjects and FlyingObjects of FlyingObjects.cs' pools. With
// a few explosions on screen they took the frame past 16.7 ms.
// Their renderers are off, and Build, just before the engine renders (Application.onBeforeRender), writes their
// sprites, each as the engine would draw it (the sprite's own mesh through its transform), into one mesh per sorting
// layer, segment of sorting orders and texture, drawn by a MeshRenderer at that layer and the lowest order in it, with
// the sprites' material. Within a mesh the sprites go in order of their sorting order, then of when they were shown
// (the engine's own order for equal ones is its own). A segment is the orders between two that another renderer of
// the layer uses (an order such a renderer uses is a segment of its own), so no other renderer draws between the
// sprites of one mesh: the orders other renderers use are recorded as the game sets them (the rewriter routes every
// Renderer.sortingOrder and sortingLayerName write of the game's to SetSortingOrder and SetSortingLayerName) and from
// a scan of every renderer shortly after each level starts (LevelStart, from Game.InitLevel), for the ones their
// prefabs set. The game's orders for these sprites are a few (0, 2, 100, 600, 9900, -999), 550-650 at random (large
// debris) or count up from 1000 (blood, bullet holes).
// The rewriter (rewriter/Patches.cs, BatchEffects) has:
// - Effect's writes (SpriteRenderer.sprite and color, the color's read, Renderer.sortingLayerName and sortingOrder,
//   Transform.localPosition, localScale and localEulerAngles, GameObject.SetActive) go to the methods below, which
//   keep them instead of the engine (Register, at the end of its constructor: the effect's transform is read once,
//   its parent's each frame).
// - SpriteObject's, FlyingObject's and FlyingObjects' writes of the sprite, colour, sorting layer and order, active
//   state and Renderer.enabled, and SpriteObject's and FlyingObject's of Transform.localPosition, localScale,
//   localEulerAngles and parent, go to the same methods, which pass them on to the engine too (the game reads some
//   of them back), except enabled, which they keep. Nothing else moves these sprites (reading each one's
//   localToWorldMatrix instead cost 1.7 us a sprite a frame). FlyingObject's constructor ends with RegisterSprite, and SpriteObject's
//   SpriteObjectStart with SpriteMade, which registers the ones FlyingObjects makes (PoolBegin and PoolEnd around
//   each of its new SpriteObject(...)), not the menus', HUD's or level's.
//   -hunt-effect-batch on|off|check   on: batched on any player; off: the game's renderers (the PS Vita's default is
//                               on, other players' off); check: both, the batched ones tinted magenta and half
//                               transparent over the game's, to see that they line up
// With -hunt-heartbeat, a HUNT_EFFECT_BATCH line every 600 frames: the sprites and vertices drawn a frame, and Build's
// time a frame in its three parts (gather: each sprite to its mesh; emit: the vertices; upload: the meshes).
// Measured in level 4-1 with -hunt-cheats god,ammo,grenadelauncher (LevelJump.cs): about 200 of these sprites in
// view, Build took 2.2 ms a frame where the engine took 4-5 ms for them as sprites; the seconds under 58 fps went
// from 23-34 of 115 to 8, under 55 from 11-17 to 3.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class EffectBatch
    {
        class Item
        {
            public GameObject go;
            public bool native;  // the engine gets the writes too (SpriteObject, FlyingObject: the game reads some back)
            public Transform parent;
            public Material material;
            public Shape shape;  // the sprite's, null for none
            public Bucket bucket;  // for the layer, segment, texture and material; null when one of them changed
            public int segments;  // the value of EffectBatch.segments the bucket was found with
            public Color32 color = new Color32(255, 255, 255, 255);
            public int layer, order;
            public Vector3 position, scale = Vector3.one, euler;
            public bool active, visible = true, listed;
            public long sortKey;
        }

        class Shape
        {
            public Vector2[] vertices, uv;
            public ushort[] triangles;
            public Texture2D texture;
            public int textureKey;
            public float radius;  // of the vertices around the pivot, for the view test
        }

        class Bucket
        {
            public MeshRenderer renderer;
            public Mesh mesh;
            public readonly List<Item> items = new List<Item>();
            public readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
            // The mesh's lists (Mesh.SetVertices and friends copy a list's count), at the frame's counts (Add and
            // RemoveRange, within a capacity kept at a power of two so that the array stays) and written through
            // their arrays (List<T>.this[] cost some 60 ns a write). Setting a list's count through reflection cost
            // 2 ms a frame; a mesh kept at a larger size, the rest of its indices degenerate, as much.
            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Vector2> uv = new List<Vector2>();
            public readonly List<Color32> colors = new List<Color32>();
            public readonly List<int> triangles = new List<int>();
            public Vector3[] vertexArray;
            public Vector2[] uvArray;
            public Color32[] colorArray;
            public int[] triangleArray;
            public int vertexCount, indexCount;
            public int order = int.MinValue;
            public int textureKey;
            public bool drawn, mixed;
        }

        class ByOrder : IComparer<Item>
        {
            public int Compare(Item a, Item b) { return a.sortKey.CompareTo(b.sortKey); }
        }

        // Unity objects by reference, hashed by Object.GetHashCode (the instance id, without GetInstanceID's main
        // thread check or Object.Equals' native one).
        class ByReference : IEqualityComparer<Object>
        {
            public bool Equals(Object a, Object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(Object o) { return o.GetHashCode(); }
        }

        // A bucket's key: its sprites' layer, segment, texture and material.
        struct Key : System.IEquatable<Key>
        {
            public int layer, segment, texture, material;

            public bool Equals(Key o) { return layer == o.layer && segment == o.segment && texture == o.texture && material == o.material; }
            public override int GetHashCode() { return ((layer * 31 + segment) * 31 + texture) * 31 + material; }
        }

        static bool? on;
        static bool check;
        static int pooling;
        static readonly ByReference Reference = new ByReference();
        static readonly ByOrder Ordering = new ByOrder();
        static readonly Dictionary<Object, Item> items = new Dictionary<Object, Item>(Reference);  // by its GameObject, Transform and renderer
        static readonly List<Item> active = new List<Item>();
        static readonly Dictionary<Object, Shape> shapes = new Dictionary<Object, Shape>(Reference);
        static readonly Dictionary<string, int> layers = new Dictionary<string, int>();
        static readonly Dictionary<Key, Bucket> buckets = new Dictionary<Key, Bucket>();
        // The orders the layer's other renderers use (sorted), by layer id, and whether one is listed.
        static readonly Dictionary<int, List<int>> taken = new Dictionary<int, List<int>>();
        static readonly HashSet<long> takenSet = new HashSet<long>();
        static int segments = 1;  // bumped when an order is taken: each item's segment is worked out again
        static int scanAt = -1, scanAgainAt = -1;
        static readonly Dictionary<long, Material> materials = new Dictionary<long, Material>();
        static readonly List<Bucket> bucketList = new List<Bucket>();
        static readonly Bounds Everywhere = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
        static Camera view;
        static bool hooked;
        static int built, registered;
        static bool logged;  // with -hunt-heartbeat: what Build drew and took, every 600 frames (HUNT_EFFECT_BATCH)
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long gatherTicks, emitTicks, uploadTicks, drawnSprites, drawnVertices;

        public static bool On
        {
            get
            {
                if (on == null)
                {
                    string value = HuntFlags.Value(ProbeFiles.Args, "-hunt-effect-batch");
                    check = value == "check";
                    on = value != null ? value == "on" || check : HuntFlags.Vita;
                    logged = HuntFlags.Has(ProbeFiles.Args, "-hunt-heartbeat");
                    if (on.Value) Debug.Log("HUNT_EFFECT_BATCH effects, debris and weather drawn as batched meshes" + (check ? ", over the game's renderers" : ""));
                }
                return on.Value;
            }
        }

        // The end of Effect's constructor: the effect's GameObject and SpriteRenderer, made, parented and inactive.
        public static void Register(GameObject go, SpriteRenderer renderer)
        {
            if (!On) return;
            Transform transform = go.transform;
            Add(go, renderer, new Item
            {
                parent = transform.parent,
                position = transform.localPosition,
                scale = transform.localScale,
                euler = transform.localEulerAngles,
            }, transform);
        }

        // The end of FlyingObject's constructor, and of SpriteObjectStart for FlyingObjects' sprites.
        public static void RegisterSprite(GameObject go, SpriteRenderer renderer)
        {
            if (!On) return;
            Transform transform = go.transform;
            Add(go, renderer, new Item
            {
                native = true,
                parent = transform.parent,
                position = transform.localPosition,
                scale = transform.localScale,
                euler = transform.localEulerAngles,
            }, transform);
        }

        public static void PoolBegin() { pooling++; }

        public static void PoolEnd() { pooling--; }

        // The end of SpriteObject.SpriteObjectStart: one FlyingObjects makes (between PoolBegin and PoolEnd) is batched.
        public static void SpriteMade(GameObject go, SpriteRenderer renderer)
        {
            if (pooling > 0) RegisterSprite(go, renderer);
        }

        static void Add(GameObject go, SpriteRenderer renderer, Item item, Transform transform)
        {
            item.go = go;
            item.material = renderer.sharedMaterial;
            item.layer = renderer.sortingLayerID;
            item.order = renderer.sortingOrder;
            item.color = renderer.color;
            item.visible = renderer.enabled;
            Sprite sprite = renderer.sprite;
            item.shape = sprite == null ? null : ShapeOf(sprite);
            if (!check) renderer.enabled = false;
            items[go] = item;
            items[transform] = item;
            items[renderer] = item;
            if (go.activeSelf) SetActive(go, true);
            registered++;
            if (!hooked)
            {
                hooked = true;
                Application.onBeforeRender += Build;
            }
        }

        static Item Find(Object target)
        {
            Item item;
            return on == true && !ReferenceEquals(target, null) && items.TryGetValue(target, out item) ? item : null;
        }

        // Whether the engine gets the write too: for what isn't batched, in check mode, and for the read-back ones.
        static bool Native(Item item)
        {
            return item == null || check || item.native;
        }

        public static void SetSprite(SpriteRenderer renderer, Sprite sprite)
        {
            Item item = Find(renderer);
            if (Native(item)) renderer.sprite = sprite;
            if (item == null) return;
            item.shape = ReferenceEquals(sprite, null) ? null : ShapeOf(sprite);
        }

        public static Color GetColor(SpriteRenderer renderer)
        {
            Item item = Find(renderer);
            return Native(item) ? renderer.color : (Color)item.color;
        }

        public static void SetColor(SpriteRenderer renderer, Color color)
        {
            Item item = Find(renderer);
            if (Native(item)) renderer.color = color;
            if (item != null) item.color = color;
        }

        public static void SetSortingLayerName(Renderer renderer, string name)
        {
            Item item = Find(renderer);
            if (Native(item)) renderer.sortingLayerName = name;
            if (on != true) return;
            int id;
            if (!layers.TryGetValue(name ?? "", out id)) layers[name ?? ""] = id = SortingLayer.NameToID(name);
            if (item == null)
            {
                Take(id, renderer.sortingOrder);
                return;
            }
            if (id != item.layer) item.bucket = null;
            item.layer = id;
        }

        public static void SetSortingOrder(Renderer renderer, int order)
        {
            Item item = Find(renderer);
            if (Native(item)) renderer.sortingOrder = order;
            if (on != true) return;
            if (item == null)
            {
                Take(renderer.sortingLayerID, order);
                return;
            }
            if (order != item.order) item.bucket = null;
            item.order = order;
        }

        public static void SetEnabled(Renderer renderer, bool value)
        {
            Item item = Find(renderer);
            if (item == null || check) renderer.enabled = value;
            if (item != null) item.visible = value;
        }

        public static void SetLocalPosition(Transform transform, Vector3 value)
        {
            Item item = Find(transform);
            if (Native(item)) transform.localPosition = value;
            if (item != null) item.position = value;
        }

        public static void SetLocalScale(Transform transform, Vector3 value)
        {
            Item item = Find(transform);
            if (Native(item)) transform.localScale = value;
            if (item != null) item.scale = value;
        }

        public static void SetLocalEulerAngles(Transform transform, Vector3 value)
        {
            Item item = Find(transform);
            if (Native(item)) transform.localEulerAngles = value;
            if (item != null) item.euler = value;
        }

        public static void SetParent(Transform transform, Transform parent)
        {
            Item item = Find(transform);
            transform.parent = parent;
            if (item != null) item.parent = parent;
        }

        public static void SetActive(GameObject go, bool value)
        {
            Item item = Find(go);
            if (Native(item)) go.SetActive(value);
            if (item == null) return;
            item.active = value;
            if (value && !item.listed)
            {
                item.listed = true;
                active.Add(item);
            }
        }

        // An order another renderer of the layer uses.
        static void Take(int layer, int order)
        {
            if (!takenSet.Add(((long)layer << 32) | (uint)order)) return;
            List<int> orders;
            if (!taken.TryGetValue(layer, out orders)) taken[layer] = orders = new List<int>();
            int at = orders.BinarySearch(order);
            orders.Insert(at < 0 ? ~at : at, order);
            segments++;
        }

        // The segment of an order in a layer: 2i for the orders between the (i-1)th and the ith taken one, 2i+1 for the
        // ith taken one itself.
        static int SegmentOf(int layer, int order)
        {
            List<int> orders;
            if (!taken.TryGetValue(layer, out orders)) return 0;
            int at = orders.BinarySearch(order);
            return at >= 0 ? 2 * at + 1 : 2 * ~at;
        }

        // The start of a level or a restart from a checkpoint (Game.InitLevel): its renderers are scanned a few frames
        // later and again a couple of seconds later, once its objects are made (some come from a coroutine).
        public static void LevelStart()
        {
            if (on != true) return;
            scanAt = Time.frameCount + 10;
            scanAgainAt = Time.frameCount + 120;
        }

        static void Scan()
        {
            foreach (Renderer renderer in Object.FindObjectsOfType<Renderer>())
                if (!items.ContainsKey(renderer) && !(renderer is MeshRenderer && renderer.name == "HuntEffectBatch"))
                    Take(renderer.sortingLayerID, renderer.sortingOrder);
            // The destroyed ones' entries (a level's weather is made again for the next).
            var gone = new List<Object>();
            foreach (KeyValuePair<Object, Item> entry in items)
                if (entry.Value.go == null) gone.Add(entry.Key);
            foreach (Object key in gone) items.Remove(key);
        }

        // Before the engine renders: the active sprites in view into the buckets' meshes.
        static void Build()
        {
            int frame = Time.frameCount;
            if (frame == scanAt || frame == scanAgainAt) Scan();
            int kept = 0;
            for (int i = 0; i < active.Count; i++)
            {
                Item item = active[i];
                if (!item.active || item.go == null)
                {
                    item.listed = false;
                    continue;
                }
                active[kept++] = item;
            }
            active.RemoveRange(kept, active.Count - kept);

            if (view == null) view = Camera.main;
            float left = float.MinValue, right = float.MaxValue, bottom = float.MinValue, top = float.MaxValue;
            if (view != null && view.orthographic)
            {
                Vector3 centre = view.transform.position;
                float half = view.orthographicSize;
                float halfWidth = half * view.aspect;
                left = centre.x - halfWidth;
                right = centre.x + halfWidth;
                bottom = centre.y - half;
                top = centre.y + half;
            }

            long t0 = clock.ElapsedTicks;
            // Each sprite in view to its bucket, with its matrix (sprite space to world).
            Transform lastParent = null;
            Matrix4x4 parentMatrix = Matrix4x4.identity;
            for (int i = 0; i < active.Count; i++)
            {
                Item item = active[i];
                Shape shape = item.shape;
                if (shape == null || !item.visible || item.color.a == 0) continue;
                Matrix4x4 m;
                {
                    if (!ReferenceEquals(item.parent, lastParent))
                    {
                        lastParent = item.parent;
                        parentMatrix = lastParent != null ? lastParent.localToWorldMatrix : Matrix4x4.identity;
                    }
                    float sx = item.scale.x, sy = item.scale.y, cos = 1f, sin = 0f;
                    if (item.euler.z != 0f)
                    {
                        float radians = item.euler.z * Mathf.Deg2Rad;
                        cos = Mathf.Cos(radians);
                        sin = Mathf.Sin(radians);
                    }
                    // parent * (translation, z rotation, scale), in x and y
                    float p00 = parentMatrix.m00, p01 = parentMatrix.m01, p10 = parentMatrix.m10, p11 = parentMatrix.m11;
                    Vector3 centre = parentMatrix.MultiplyPoint3x4(item.position);
                    m = Matrix4x4.identity;
                    m.m00 = sx * (p00 * cos + p01 * sin);
                    m.m01 = sy * (p01 * cos - p00 * sin);
                    m.m10 = sx * (p10 * cos + p11 * sin);
                    m.m11 = sy * (p11 * cos - p10 * sin);
                    m.m03 = centre.x;
                    m.m13 = centre.y;
                    m.m23 = centre.z;
                }
                float reach = shape.radius * Mathf.Max(Mathf.Abs(m.m00) + Mathf.Abs(m.m01), Mathf.Abs(m.m10) + Mathf.Abs(m.m11));
                if (m.m03 + reach < left || m.m03 - reach > right || m.m13 + reach < bottom || m.m13 - reach > top) continue;
                Bucket bucket = item.bucket;
                if (bucket == null || item.segments != segments || bucket.textureKey != shape.textureKey)
                {
                    item.bucket = bucket = BucketOf(item, shape);
                    item.segments = segments;
                }
                if (bucket.items.Count > 0 && bucket.items[bucket.items.Count - 1].order != item.order) bucket.mixed = true;
                item.sortKey = ((long)item.order << 32) | (uint)bucket.items.Count;
                bucket.items.Add(item);
                bucket.matrices.Add(m);
            }

            long t1 = clock.ElapsedTicks;
            long upload = 0;
            for (int i = 0; i < bucketList.Count; i++)
            {
                Bucket bucket = bucketList[i];
                int count = bucket.items.Count;
                drawnSprites += count;
                if (count > 0)
                {
                    if (bucket.mixed)
                    {
                        // In sorting order; the matrices follow through the items' insertion index.
                        bucket.items.Sort(Ordering);
                    }
                    int lowest = bucket.items[0].order;
                    int vertexNeed = 0, indexNeed = 0;
                    for (int k = 0; k < count; k++)
                    {
                        vertexNeed += bucket.items[k].shape.vertices.Length;
                        indexNeed += bucket.items[k].shape.triangles.Length;
                    }
                    Reserve(bucket, vertexNeed, indexNeed);
                    Vector3[] outVertices = bucket.vertexArray;
                    Vector2[] outUv = bucket.uvArray;
                    Color32[] outColors = bucket.colorArray;
                    int[] outTriangles = bucket.triangleArray;
                    int nv = 0, nt = 0;
                    for (int k = 0; k < count; k++)
                    {
                        Item item = bucket.items[k];
                        if (item.order < lowest) lowest = item.order;
                        Matrix4x4 m = bucket.matrices[(int)(item.sortKey & 0xFFFFFFFF)];
                        Shape shape = item.shape;
                        int first = nv;
                        Vector2[] vertices = shape.vertices, uv = shape.uv;
                        Color32 color = check ? new Color32(255, 0, 255, (byte)(item.color.a / 2)) : item.color;
                        float m00 = m.m00, m01 = m.m01, m03 = m.m03, m10 = m.m10, m11 = m.m11, m13 = m.m13, z = m.m23;
                        for (int v = 0; v < vertices.Length; v++, nv++)
                        {
                            float x = vertices[v].x, y = vertices[v].y;
                            outVertices[nv] = new Vector3(m03 + m00 * x + m01 * y, m13 + m10 * x + m11 * y, z);
                            outUv[nv] = uv[v];
                            outColors[nv] = color;
                        }
                        ushort[] triangles = shape.triangles;
                        // A negative scale on one axis mirrors the sprite, and its triangles' winding: the sprite shader culls nothing.
                        for (int t = 0; t < triangles.Length; t++) outTriangles[nt++] = first + triangles[t];
                    }
                    bucket.vertexCount = nv;
                    bucket.indexCount = nt;
                    if (lowest != bucket.order)
                    {
                        bucket.order = lowest;
                        bucket.renderer.sortingOrder = lowest;
                    }
                }
                else
                {
                    bucket.vertexCount = bucket.indexCount = 0;
                    if (bucket.drawn) Reserve(bucket, 0, 0);
                }
                bool draw = bucket.indexCount > 0;
                drawnVertices += bucket.vertexCount;
                long u0 = clock.ElapsedTicks;
                if (draw || bucket.drawn) bucket.mesh.Clear();
                if (draw)
                {
                    bucket.mesh.SetVertices(bucket.vertices);
                    bucket.mesh.SetUVs(0, bucket.uv);
                    bucket.mesh.SetColors(bucket.colors);
                    bucket.mesh.SetTriangles(bucket.triangles, 0, false);
                    bucket.mesh.bounds = Everywhere;
                }
                if (draw != bucket.drawn) bucket.renderer.enabled = draw;
                bucket.drawn = draw;
                upload += clock.ElapsedTicks - u0;
                bucket.items.Clear();
                bucket.matrices.Clear();
                bucket.mixed = false;
            }
            long t2 = clock.ElapsedTicks;
            gatherTicks += t1 - t0;
            emitTicks += t2 - t1 - upload;
            uploadTicks += upload;
            if (++built % 600 == 0 && logged)
            {
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / 600;
                Debug.Log("HUNT_EFFECT_BATCH " + registered + " registered, " + active.Count + " active, " + bucketList.Count + " meshes; per frame: " +
                          (drawnSprites / 600) + " sprites, " + (drawnVertices / 600) + " vertices, gather " + (gatherTicks * ms).ToString("F2") +
                          " ms, emit " + (emitTicks * ms).ToString("F2") + " ms, upload " + (uploadTicks * ms).ToString("F2") + " ms");
                gatherTicks = emitTicks = uploadTicks = drawnSprites = drawnVertices = 0;
            }
        }

        // The lists at these counts, and their arrays.
        static void Reserve(Bucket bucket, int vertices, int indices)
        {
            bucket.vertexArray = Sized(bucket.vertices, vertices, bucket.vertexArray);
            bucket.uvArray = Sized(bucket.uv, vertices, bucket.uvArray);
            bucket.colorArray = Sized(bucket.colors, vertices, bucket.colorArray);
            bucket.triangleArray = Sized(bucket.triangles, indices, bucket.triangleArray);
        }

        static T[] Sized<T>(List<T> list, int count, T[] array)
        {
            if (list.Capacity < count)
            {
                list.Capacity = Mathf.Max(64, Mathf.NextPowerOfTwo(count));
                array = null;
            }
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            while (list.Count < count) list.Add(default(T));
            return array ?? ListArray<T>.Of(list);
        }

        // A list's array, which List<T> keeps private (mscorlib's _items).
        static class ListArray<T>
        {
            static readonly System.Reflection.FieldInfo Items =
                typeof(List<T>).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            public static T[] Of(List<T> list)
            {
                return (T[])Items.GetValue(list);
            }
        }

        static Shape ShapeOf(Sprite sprite)
        {
            Shape shape;
            if (shapes.TryGetValue(sprite, out shape)) return shape;
            Vector2[] vertices = sprite.vertices;
            float radius = 0f;
            foreach (Vector2 v in vertices) radius = Mathf.Max(radius, v.magnitude);
            Texture2D texture = sprite.texture;
            shapes[sprite] = shape = new Shape
            {
                vertices = vertices, uv = sprite.uv, triangles = sprite.triangles, texture = texture, radius = radius,
                textureKey = texture != null ? texture.GetInstanceID() : 0,
            };
            return shape;
        }

        static Bucket BucketOf(Item item, Shape shape)
        {
            int materialKey = item.material != null ? item.material.GetInstanceID() : 0;
            var key = new Key { layer = item.layer, segment = SegmentOf(item.layer, item.order), texture = shape.textureKey, material = materialKey };
            Bucket bucket;
            if (buckets.TryGetValue(key, out bucket)) return bucket;
            var go = new GameObject("HuntEffectBatch");
            Object.DontDestroyOnLoad(go);
            var mesh = new Mesh { name = "HuntEffectBatch" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = MaterialFor(item.material, materialKey, shape);
            renderer.sortingLayerID = item.layer;
            renderer.sortingOrder = item.order;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            // The sprite shader's per-sprite constants, which a SpriteRenderer sets for each draw and a MeshRenderer doesn't.
            var block = new MaterialPropertyBlock();
            block.SetColor("_RendererColor", Color.white);
            block.SetVector("_Flip", Vector4.one);
            renderer.SetPropertyBlock(block);
            renderer.enabled = false;
            bucket = new Bucket { renderer = renderer, mesh = mesh, order = item.order, textureKey = shape.textureKey };
            buckets[key] = bucket;
            bucketList.Add(bucket);
            return bucket;
        }

        static Material MaterialFor(Material spriteMaterial, int materialKey, Shape shape)
        {
            long key = ((long)materialKey << 32) ^ (uint)shape.textureKey;
            Material material;
            if (!materials.TryGetValue(key, out material))
            {
                material = spriteMaterial != null ? new Material(spriteMaterial) : new Material(Shader.Find("Sprites/Default"));
                material.mainTexture = shape.texture;
                materials[key] = material;
            }
            return material;
        }
    }
}
