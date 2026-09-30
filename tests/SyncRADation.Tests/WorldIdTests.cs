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

        [Fact]
        public void DebugLabel_is_16_digit_upper_hex_then_path()
        {
            var go = new GameObject("X", "S", null, 0);
            Assert.Equal("00000000000000FF X[0]", WorldId.DebugLabel(0xFF, go.transform));
            Assert.Equal("00000000000000FF ?", WorldId.DebugLabel(0xFF, null));
        }
    }
}
