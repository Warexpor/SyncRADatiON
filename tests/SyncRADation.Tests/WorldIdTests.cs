using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace SyncRADation.Tests
{
    public class WorldIdTests
    {
        // Independent reference: standard FNV-1a 64 over the UTF-8 bytes of scene + '\0' + path (ASCII inputs),
        // vectors computed outside the game code.
        [Theory]
        [InlineData("", "", 0xAF63BD4C8601B7DFUL)]
        [InlineData("a", "", 0x089BE207B544F1E4UL)]
        [InlineData("", "a", 0x08326707B4EB37DAUL)]
        [InlineData("scene", "path", 0x2CBEE7EB88B3B172UL)]
        [InlineData("MED_Hospital", "Root[0]/Door[3]", 0x05037FC03E41D4B8UL)]
        [InlineData("spawn", "SR_Spawn_1", 0xB0BD358B1C895DC9UL)]
        public void Compute_matches_fixed_FNV1a64_vectors(string scene, string path, ulong expected)
        {
            Assert.Equal(expected, WorldId.Compute(scene, path));
        }

        [Fact]
        public void Compute_is_FNV1a64_over_scene_NUL_path()
        {
            Assert.Equal(0x8A5F40D8243888AAUL, WorldId.Compute("foo", "bar")); // FNV-1a64("foo\0bar")
        }

        [Fact]
        public void Compute_null_equals_empty_and_empty_is_offset_times_prime()
        {
            const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
            Assert.Equal(unchecked(offset * prime), WorldId.Compute(null, null));
            Assert.Equal(WorldId.Compute("", ""), WorldId.Compute(null, null));
            Assert.Equal(WorldId.Compute("", "x"), WorldId.Compute(null, "x"));
        }

        [Fact]
        public void Compute_separates_scene_and_path_with_a_NUL_so_boundaries_matter()
        {
            Assert.NotEqual(WorldId.Compute("ab", "c"), WorldId.Compute("a", "bc"));
            Assert.NotEqual(WorldId.Compute("a", "b"), WorldId.Compute("b", "a"));
        }

        [Fact]
        public void Compute_hashes_UTF16_code_units_not_bytes()
        {
            Assert.Equal(0x05A6151C7C2CA498UL, WorldId.Compute("Ü", "é"));
            Assert.Equal(0x0C20B57D56D899CBUL, WorldId.Compute("€", "x"));
        }

        // ------------------------------------------------------------------ hierarchy path

        [Fact]
        public void Path_of_null_is_empty_and_id_of_null_is_zero()
        {
            Assert.Equal("", WorldId.GetHierarchyPath(null));
            Assert.Equal(0UL, WorldId.FromTransform(null));
            Assert.Equal(0UL, WorldId.FromGameObject(null));
        }

        [Fact]
        public void Path_appends_sibling_index_to_every_segment()
        {
            var root = new GameObject("Root", "S", null, 0);
            var door = new GameObject("Door", "S", root.transform);
            Assert.Equal("Root[0]", WorldId.GetHierarchyPath(root.transform));
            Assert.Equal("Root[0]/Door[0]", WorldId.GetHierarchyPath(door.transform));
        }

        [Fact]
        public void Same_named_siblings_are_disambiguated_by_sibling_index()
        {
            var root = new GameObject("Root", "S", null, 4);
            var a = new GameObject("Door", "S", root.transform);
            var b = new GameObject("Door", "S", root.transform);
            var c = new GameObject("Door", "S", root.transform);
            var deep = new GameObject("Handle", "S", c.transform);

            Assert.Equal("Root[4]/Door[0]", WorldId.GetHierarchyPath(a.transform));
            Assert.Equal("Root[4]/Door[1]", WorldId.GetHierarchyPath(b.transform));
            Assert.Equal("Root[4]/Door[2]/Handle[0]", WorldId.GetHierarchyPath(deep.transform));
            Assert.NotEqual(WorldId.FromTransform(a.transform), WorldId.FromTransform(b.transform));
            Assert.NotEqual(WorldId.FromTransform(b.transform), WorldId.FromTransform(c.transform));
        }

        [Fact]
        public void FromTransform_is_Compute_of_scene_and_path_and_is_stable_across_calls()
        {
            var root = new GameObject("Level", "MED_Hospital", null, 0);
            var door = new GameObject("Door", "MED_Hospital", root.transform);
            ulong id = WorldId.FromTransform(door.transform);
            Assert.Equal(WorldId.Compute("MED_Hospital", "Level[0]/Door[0]"), id);
            Assert.Equal(id, WorldId.FromGameObject(door));
            Assert.Equal(id, WorldId.FromTransform(door.transform));
        }

        [Fact]
        public void Same_path_in_different_scenes_gives_different_ids()
        {
            var a = new GameObject("Door", "SceneA", null, 0);
            var b = new GameObject("Door", "SceneB", null, 0);
            Assert.NotEqual(WorldId.FromGameObject(a), WorldId.FromGameObject(b));
        }

        [Fact]
        public void SR_Spawn_objects_use_the_scene_independent_spawn_id()
        {
            var a = new GameObject("SR_Spawn_7", "SceneA", null, 0);
            var b = new GameObject("SR_Spawn_7", "SceneB", null, 3);
            Assert.Equal(WorldId.Compute("spawn", "SR_Spawn_7"), WorldId.FromGameObject(a));
            Assert.Equal(WorldId.FromGameObject(a), WorldId.FromGameObject(b));
        }

        [Fact]
        public void Empty_object_scene_falls_back_to_the_active_scene()
        {
            string old = SceneManager.ActiveSceneName;
            try
            {
                SceneManager.ActiveSceneName = "ActiveOne";
                var go = new GameObject("X", "", null, 0);
                Assert.Equal(WorldId.Compute("ActiveOne", "X[0]"), WorldId.FromGameObject(go));
            }
            finally { SceneManager.ActiveSceneName = old; }
        }

        // ------------------------------------------------------------------ scene cache (pinned at load)

        [Fact]
        public void Warm_gives_exactly_the_live_id_for_every_node()
        {
            WorldId.BeginScene("WarmScene");
            var level = new GameObject("Level", "WarmScene", null, 2);
            var room = new GameObject("Room", "WarmScene", level.transform);
            var a = new GameObject("Pickup", "WarmScene", room.transform);
            var b = new GameObject("Pickup", "WarmScene", room.transform);
            var deep = new GameObject("Handle", "WarmScene", b.transform);
            var spawn = new GameObject("SR_Spawn_3_EULR", "WarmScene", room.transform);
            var spawnChild = new GameObject("Body", "WarmScene", spawn.transform);
            var nodes = new[] { level, room, a, b, deep, spawn, spawnChild };
            var expected = new ulong[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
                expected[i] = WorldId.ComputeLive(nodes[i].transform);

            var pass = new WorldId.WarmPass();
            // Leaves first: ancestors are filled from the memo, not re-read.
            for (int i = nodes.Length - 1; i >= 0; i--)
                WorldId.Warm(nodes[i], pass);

            Assert.Equal(nodes.Length, pass.Added);
            for (int i = 0; i < nodes.Length; i++)
                Assert.Equal(expected[i], WorldId.FromGameObject(nodes[i]));
            Assert.Equal(WorldId.Compute("spawn", "SR_Spawn_3_EULR"), WorldId.FromGameObject(spawn));
            Assert.Equal(WorldId.Compute("WarmScene", "Level[2]/Room[0]/SR_Spawn_3_EULR[2]/Body[0]"), WorldId.FromGameObject(spawnChild));
        }

        [Fact]
        public void Pinned_id_survives_a_destroyed_earlier_sibling()
        {
            WorldId.BeginScene("PinScene");
            var room = new GameObject("Room", "PinScene", null, 0);
            var first = new GameObject("Pickup", "PinScene", room.transform);
            var second = new GameObject("Pickup", "PinScene", room.transform);
            var pass = new WorldId.WarmPass();
            WorldId.Warm(first, pass);
            WorldId.Warm(second, pass);
            ulong pinned = WorldId.FromGameObject(second);

            first.transform.DestroyForTest(); // the taker's native ItemPickup.release
            Assert.NotEqual(pinned, WorldId.ComputeLive(second.transform)); // the live path shifted: Pickup[1] -> Pickup[0]
            Assert.Equal(pinned, WorldId.FromGameObject(second));
            Assert.Equal(pinned, WorldId.FromTransform(second.transform));
        }

        [Fact]
        public void Forget_recomputes_and_Pin_overrides()
        {
            WorldId.BeginScene("ForgetScene");
            var go = new GameObject("Enemy", "ForgetScene", null, 0);
            ulong live = WorldId.FromGameObject(go);
            go.transform.name = "SR_Spawn_9_STAR"; // adoption rename
            Assert.Equal(live, WorldId.FromGameObject(go));
            WorldId.Forget(go);
            Assert.Equal(WorldId.Compute("spawn", "SR_Spawn_9_STAR"), WorldId.FromGameObject(go));
            WorldId.Pin(go, 0x1234UL);
            Assert.Equal(0x1234UL, WorldId.FromGameObject(go));
        }

        [Fact]
        public void A_new_scene_drops_pinned_ids_and_a_warm_never_overwrites_one()
        {
            WorldId.BeginScene("SceneOne");
            var go = new GameObject("X", "SceneOne", null, 0);
            WorldId.Pin(go, 42UL);
            WorldId.Warm(go, new WorldId.WarmPass());
            Assert.Equal(42UL, WorldId.FromGameObject(go)); // second warm (mid-scene rescan) keeps the load-time id
            WorldId.BeginScene("SceneOne");
            Assert.Equal(42UL, WorldId.FromGameObject(go)); // same scene (additive load / reload): kept
            WorldId.BeginScene("SceneTwo");
            Assert.Equal(WorldId.ComputeLive(go.transform), WorldId.FromGameObject(go));
        }
    }
}
