using UnityEngine;

// How many debris pieces the game keeps (FlyingObjects' pool: shards, stones, glass, each a SpriteRenderer). The
// game makes 256, and a piece lies where it lands for 8 s before it fades. On the PS Vita the engine spends about
// 50 us a frame on each visible sprite, whatever the game's code does with it: at Sweet Angel's arena in level
// 1-1, the police cars' and windows' debris put 240 pieces on screen, 10-12 ms a frame, and the frame rate fell
// to 41-52 fps (HUNT_VISIBLE: Debris@Effects=244; the game's own code took 3 ms of the 20-23 ms frames,
// compat/Prof.cs). With fewer pieces, an explosion throws as many as it did while the pool has room, and when it
// is full the game takes the oldest piece, as it does at 256 (FlyingObjects.StartDebris): old shards on the
// ground give way to new ones sooner. The rewriter (rewriter/Patches.cs, PatchDebrisPool) has the pool's size
// go through Pool.
//   -hunt-debris <n>|game   the pool's size: n pieces, or game (256); the PS Vita's default is 128, others' game
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class Debris
    {
        const int VitaPool = 128;

        public static int Pool(int game)
        {
            string value = HuntFlags.Value(ProbeFiles.Args, "-hunt-debris");
            int size;
            if (value == "game") size = game;
            else if (value != null && int.TryParse(value, out size) && size > 0) size = Mathf.Min(size, game);
            else size = HuntFlags.Vita ? Mathf.Min(VitaPool, game) : game;
            if (size != game) Debug.Log("HUNT_DEBRIS " + size + " debris pieces (the game's " + game + ")");
            return size;
        }
    }
}
