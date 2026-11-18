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
// With -hunt-heartbeat, a HUNT_EFFECT_BATCH line at each level's scans (what Purge dropped) and every 600 frames: the sprites and vertices drawn a frame, and Build's
// time a frame in its three parts (gather: each sprite to its mesh; emit: the vertices; upload: the meshes), and how
// many meshes were kept as they were or had only their vertices uploaded.
// Measured in level 4-1 with -hunt-cheats god,ammo,grenadelauncher (LevelJump.cs): about 200 of these sprites in
// view, Build took 2.2 ms a frame where the engine took 4-5 ms for them as sprites; the seconds under 58 fps went
// from 23-34 of 115 to 8, under 55 from 11-17 to 3. Build's cost since: about 0.7 ms per 100 sprites in 4-1 and 0.55
// in 3-3, from 1.2-1.3 and 0.9-1.0 (gather a third of what it was: arrays instead of lists, no Matrix4x4 or Mathf
// calls, the rotation's sine and cosine kept, an exact compare instead of a hash of floats; upload half: no
// Mesh.Clear, and the uv and triangles kept when the shapes are the same).
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
            // Build's: the sprite's matrix (sprite space to world, in x and y, and its z) and shape, colour and order as
            // last gathered, to tell an unchanged sprite; the cosine and sine of euler.z, worked out again when it changes.
            public float m00, m01, m10, m11, x, y, z;
            public Shape drawnShape;
            public int drawnColor, drawnOrder;
            public float angle, cos = 1f, sin;
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
            public Key key;
            public MeshRenderer renderer;
            public Mesh mesh;
            public Material material;
            // The frame's sprites (List<T>'s indexer cost some 60 ns a call), each with its matrix on it.
            public Item[] items = new Item[16];
            public int count, lastOrder;
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
            // What the mesh was made from: its sprites in the order they came (lastItems), and its shapes and colours in
            // the mesh's order. The same sprites in the same order, none changed (changed), and the mesh is kept as it
            // is (resting blood, bullet holes); the same shapes, and only the vertices, and the colours if they
            // changed, are uploaded again (flying debris, falling snow): the uv and triangles stay.
            public Item[] lastItems = new Item[16];
            public Shape[] meshShapes = new Shape[16];
            public int[] meshColors = new int[16];
            public int lastCount, meshCount, meshVertices;
            public bool drawn, mixed, changed;
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
        static long gatherTicks, emitTicks, uploadTicks, drawnSprites, drawnVertices, unchanged, vertexOnly;

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
            Purge();
        }

        // The meshes nothing drew into last frame (the last level's, mostly: its weather, its debris) go, with the
        // materials only they used, and the shapes no registered sprite has: kept, they held on to their sprites'
        // textures from level to level, and Build went through every mesh each frame. Each item finds its mesh
        // again (segments), the ones still drawn by their keys.
        static void Purge()
        {
            int kept = 0, meshesBefore = bucketList.Count, materialsBefore = materials.Count, shapesBefore = shapes.Count;
            buckets.Clear();
            var materialsKept = new HashSet<Material>();
            for (int i = 0; i < bucketList.Count; i++)
            {
                Bucket bucket = bucketList[i];
                if (bucket.drawn)
                {
                    bucketList[kept++] = bucket;
                    buckets[bucket.key] = bucket;
                    materialsKept.Add(bucket.material);
                    continue;
                }
                Object.Destroy(bucket.renderer.gameObject);
                Object.Destroy(bucket.mesh);
            }
            bucketList.RemoveRange(kept, bucketList.Count - kept);
            segments++;

            var materialKeys = new List<long>();
            foreach (KeyValuePair<long, Material> entry in materials)
                if (!materialsKept.Contains(entry.Value)) materialKeys.Add(entry.Key);
            foreach (long key in materialKeys)
            {
                Object.Destroy(materials[key]);
                materials.Remove(key);
            }

            var shapesKept = new HashSet<Shape>();
            foreach (Item item in items.Values)
                if (item.shape != null) shapesKept.Add(item.shape);
            var sprites = new List<Object>();
            foreach (KeyValuePair<Object, Shape> entry in shapes)
                if (!shapesKept.Contains(entry.Value)) sprites.Add(entry.Key);
            foreach (Object sprite in sprites) shapes.Remove(sprite);
            if (logged)
                Debug.Log("HUNT_EFFECT_BATCH dropped " + (meshesBefore - bucketList.Count) + " of " + meshesBefore + " meshes, " +
                          (materialsBefore - materials.Count) + " of " + materialsBefore + " materials, " +
                          (shapesBefore - shapes.Count) + " of " + shapesBefore + " shapes");
        }

        // Before the engine renders: the active sprites in view into the buckets' meshes.
        static void Build()
        {
            int frame = Time.frameCount;
            if (frame == scanAt || frame == scanAgainAt) Scan();
            Item[] list = ListArray<Item>.Of(active);
            int listed = active.Count, kept = 0;
            for (int i = 0; i < listed; i++)
            {
                Item item = list[i];
                if (!item.active || item.go == null)
                {
                    item.listed = false;
                    continue;
                }
                list[kept++] = item;
            }
            active.RemoveRange(kept, listed - kept);

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
            float p00 = 1f, p01 = 0f, p02 = 0f, p03 = 0f, p10 = 0f, p11 = 1f, p12 = 0f, p13 = 0f, p20 = 0f, p21 = 0f, p22 = 1f, p23 = 0f;
            for (int i = 0; i < kept; i++)
            {
                Item item = list[i];
                Shape shape = item.shape;
                if (shape == null || !item.visible || item.color.a == 0) continue;
                if (!ReferenceEquals(item.parent, lastParent))
                {
                    lastParent = item.parent;
                    Matrix4x4 p = lastParent != null ? lastParent.localToWorldMatrix : Matrix4x4.identity;
                    p00 = p.m00; p01 = p.m01; p02 = p.m02; p03 = p.m03;
                    p10 = p.m10; p11 = p.m11; p12 = p.m12; p13 = p.m13;
                    p20 = p.m20; p21 = p.m21; p22 = p.m22; p23 = p.m23;
                }
                float sx = item.scale.x, sy = item.scale.y, cos = 1f, sin = 0f;
                float angle = item.euler.z;
                if (angle != 0f)
                {
                    if (angle != item.angle)
                    {
                        item.angle = angle;
                        float radians = angle * Mathf.Deg2Rad;
                        item.cos = Mathf.Cos(radians);
                        item.sin = Mathf.Sin(radians);
                    }
                    cos = item.cos;
                    sin = item.sin;
                }
                // parent * (translation, z rotation, scale), in x and y
                float px = item.position.x, py = item.position.y, pz = item.position.z;
                float x = p00 * px + p01 * py + p02 * pz + p03;
                float y = p10 * px + p11 * py + p12 * pz + p13;
                float z = p20 * px + p21 * py + p22 * pz + p23;
                float m00 = sx * (p00 * cos + p01 * sin);
                float m01 = sy * (p01 * cos - p00 * sin);
                float m10 = sx * (p10 * cos + p11 * sin);
                float m11 = sy * (p11 * cos - p10 * sin);
                float reachX = (m00 < 0f ? -m00 : m00) + (m01 < 0f ? -m01 : m01);
                float reachY = (m10 < 0f ? -m10 : m10) + (m11 < 0f ? -m11 : m11);
                float reach = shape.radius * (reachX > reachY ? reachX : reachY);
                if (x + reach < left || x - reach > right || y + reach < bottom || y - reach > top) continue;
                Bucket bucket = item.bucket;
                if (bucket == null || item.segments != segments || bucket.textureKey != shape.textureKey)
                {
                    item.bucket = bucket = BucketOf(item, shape);
                    item.segments = segments;
                }
                int order = item.order;
                Color32 c = item.color;
                int color = c.r | c.g << 8 | c.b << 16 | c.a << 24;
                bool same = m00 == item.m00 && m01 == item.m01 && m10 == item.m10 && m11 == item.m11 && x == item.x && y == item.y &&
                            z == item.z && ReferenceEquals(shape, item.drawnShape) && color == item.drawnColor && order == item.drawnOrder;
                if (!same)
                {
                    item.m00 = m00; item.m01 = m01; item.m10 = m10; item.m11 = m11;
                    item.x = x; item.y = y; item.z = z;
                    item.drawnShape = shape;
                    item.drawnColor = color;
                    item.drawnOrder = order;
                }
                int n = bucket.count;
                if (n > 0 && bucket.lastOrder != order) bucket.mixed = true;
                bucket.lastOrder = order;
                item.sortKey = ((long)order << 32) | (uint)n;
                if (n == bucket.items.Length) System.Array.Resize(ref bucket.items, 2 * n);
                bucket.items[n] = item;
                bucket.count = n + 1;
                if (!same || n >= bucket.lastCount || !ReferenceEquals(bucket.lastItems[n], item)) bucket.changed = true;
            }

            long t1 = clock.ElapsedTicks;
            long upload = 0;
            for (int i = 0; i < bucketList.Count; i++)
            {
                Bucket bucket = bucketList[i];
                int count = bucket.count;
                if (count == 0 && !bucket.drawn) continue;
                drawnSprites += count;
                bool mixed = bucket.mixed;
                bucket.count = 0;
                bucket.mixed = false;
                if (count > 0 && bucket.drawn && count == bucket.lastCount && !bucket.changed)
                {
                    // Nothing changed: the mesh stays.
                    unchanged++;
                    continue;
                }
                bucket.changed = false;
                bool sameShapes = false, sameColors = false;
                if (count > 0)
                {
                    if (bucket.lastItems.Length < count) bucket.lastItems = new Item[bucket.items.Length];
                    System.Array.Copy(bucket.items, bucket.lastItems, count);
                    if (mixed) System.Array.Sort(bucket.items, 0, count, Ordering);  // in sorting order
                    Emit(bucket, count, out sameShapes, out sameColors);
                }
                else bucket.vertexCount = bucket.indexCount = 0;
                bucket.lastCount = count;
                bool draw = bucket.indexCount > 0;
                drawnVertices += bucket.vertexCount;
                long u0 = clock.ElapsedTicks;
                if (draw)
                {
                    if (sameShapes)
                    {
                        // The same vertex count: the uv and triangles stay.
                        bucket.mesh.SetVertices(bucket.vertices);
                        if (!sameColors) bucket.mesh.SetColors(bucket.colors);
                    }
                    else if (bucket.vertexCount < bucket.meshVertices)
                    {
                        // Fewer vertices than the mesh has: the triangles first, so that each set leaves the mesh's
                        // indices within its vertices (no Clear, which drops the mesh's buffers).
                        bucket.mesh.SetTriangles(bucket.triangles, 0, false);
                        bucket.mesh.SetVertices(bucket.vertices);
                        bucket.mesh.SetUVs(0, bucket.uv);
                        bucket.mesh.SetColors(bucket.colors);
                    }
                    else
                    {
                        bucket.mesh.SetVertices(bucket.vertices);
                        bucket.mesh.SetUVs(0, bucket.uv);
                        bucket.mesh.SetColors(bucket.colors);
                        bucket.mesh.SetTriangles(bucket.triangles, 0, false);
                    }
                    bucket.meshVertices = bucket.vertexCount;
                    bucket.mesh.bounds = Everywhere;
                }
                else
                {
                    if (bucket.drawn) bucket.mesh.Clear();
                    bucket.meshCount = bucket.meshVertices = 0;
                }
                if (draw != bucket.drawn) bucket.renderer.enabled = draw;
                bucket.drawn = draw;
                upload += clock.ElapsedTicks - u0;
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
                          " ms, emit " + (emitTicks * ms).ToString("F2") + " ms, upload " + (uploadTicks * ms).ToString("F2") + " ms, " +
                          (unchanged / 600) + " meshes kept as they were, " + (vertexOnly / 600) + " with only their vertices uploaded");
                gatherTicks = emitTicks = uploadTicks = drawnSprites = drawnVertices = unchanged = vertexOnly = 0;
            }
        }

        // A bucket's sprites (in sorting order, count > 0) into its mesh's lists: all of it, or, with the same shapes in
        // the same order as the mesh has (sameShapes), only the vertices and colours (sameColors: those didn't change).
        static unsafe void Emit(Bucket bucket, int count, out bool sameShapes, out bool sameColors)
        {
            Item[] items = bucket.items;
            Shape[] meshShapes = bucket.meshShapes;
            bool shapes = bucket.drawn && count == bucket.meshCount;
            int lowest = items[0].order, vertexNeed = 0, indexNeed = 0;
            for (int k = 0; k < count; k++)
            {
                Item item = items[k];
                Shape shape = item.shape;
                if (item.order < lowest) lowest = item.order;
                vertexNeed += shape.vertices.Length;
                indexNeed += shape.triangles.Length;
                if (shapes && !ReferenceEquals(meshShapes[k], shape)) shapes = false;
            }
            if (!shapes)
            {
                Reserve(bucket, vertexNeed, indexNeed);
                if (meshShapes.Length < count)
                {
                    bucket.meshShapes = meshShapes = new Shape[items.Length];
                    bucket.meshColors = new int[items.Length];
                }
            }
            else vertexOnly++;
            int[] meshColors = bucket.meshColors;
            bool colors = shapes;
            int nv = 0, nt = 0;
            fixed (Vector3* outVertices = bucket.vertexArray)
            fixed (Vector2* outUv = bucket.uvArray)
            fixed (Color32* outColors = bucket.colorArray)
            fixed (int* outTriangles = bucket.triangleArray)
            {
                for (int k = 0; k < count; k++)
                {
                    Item item = items[k];
                    Shape shape = item.shape;
                    Color32 color = check ? new Color32(255, 0, 255, (byte)(item.color.a / 2)) : item.color;
                    int packed = color.r | color.g << 8 | color.b << 16 | color.a << 24;
                    if (packed != meshColors[k]) colors = false;
                    meshColors[k] = packed;
                    meshShapes[k] = shape;
                    int first = nv;
                    float m00 = item.m00, m01 = item.m01, m03 = item.x, m10 = item.m10, m11 = item.m11, m13 = item.y, z = item.z;
                    Vector2[] vertices = shape.vertices;
                    int length = vertices.Length;
                    fixed (Vector2* from = vertices)
                    {
                        for (int v = 0; v < length; v++, nv++)
                        {
                            float x = from[v].x, y = from[v].y;
                            outVertices[nv] = new Vector3(m03 + m00 * x + m01 * y, m13 + m10 * x + m11 * y, z);
                            outColors[nv] = color;
                        }
                    }
                    if (shapes) continue;
                    fixed (Vector2* uv = shape.uv)
                        for (int v = 0; v < length; v++) outUv[first + v] = uv[v];
                    ushort[] triangles = shape.triangles;
                    int indices = triangles.Length;
                    // A negative scale on one axis mirrors the sprite, and its triangles' winding: the sprite shader culls nothing.
                    fixed (ushort* from = triangles)
                        for (int t = 0; t < indices; t++) outTriangles[nt++] = first + from[t];
                }
            }
            bucket.meshCount = count;
            if (!shapes)
            {
                bucket.vertexCount = nv;
                bucket.indexCount = nt;
            }
            sameShapes = shapes;
            sameColors = colors;
            if (lowest != bucket.order)
            {
                bucket.order = lowest;
                bucket.renderer.sortingOrder = lowest;
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
            Material material = MaterialFor(item.material, materialKey, shape);
            renderer.sharedMaterial = material;
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
            bucket = new Bucket { key = key, renderer = renderer, mesh = mesh, material = material, order = item.order, textureKey = shape.textureKey };
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
