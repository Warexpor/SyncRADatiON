// Minimal source-compatible stand-ins for the UnityEngine surface used by the pure game files
// (NetMessages.cs: Quaternion/Mathf; WorldId.cs: Transform/GameObject/SceneManager). Test-only.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);
    }

    public static class Mathf
    {
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Clamp(float value, float min, float max) => value < min ? min : (value > max ? max : value);
        // Same formula as UnityEngine.Mathf.Approximately (PuzzleMerge.cs).
        public static bool Approximately(float a, float b)
            => Math.Abs(b - a) < Math.Max(1E-06f * Math.Max(Math.Abs(a), Math.Abs(b)), float.Epsilon * 8f);
    }

    public class GameObject
    {
        public Transform transform { get; }
        public SceneManagement.Scene scene { get; set; }

        public GameObject(string name, string sceneName = "", Transform parent = null, int? rootSiblingIndex = null)
        {
            transform = new Transform(this, name, parent, rootSiblingIndex);
            scene = new SceneManagement.Scene { name = sceneName };
        }
    }

    public class Transform
    {
        private readonly List<Transform> _children = new List<Transform>();
        private readonly int _rootIndex;

        public string name { get; set; }
        public Transform parent { get; }
        public GameObject gameObject { get; }

        internal Transform(GameObject go, string name, Transform parent, int? rootSiblingIndex)
        {
            gameObject = go;
            this.name = name;
            this.parent = parent;
            _rootIndex = rootSiblingIndex ?? 0;
            parent?._children.Add(this);
        }

        public int GetSiblingIndex() => parent == null ? _rootIndex : parent._children.IndexOf(this);
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name;
    }

    public static class SceneManager
    {
        /// <summary>Test hook: what GetActiveScene().name returns.</summary>
        public static string ActiveSceneName = "";
        public static Scene GetActiveScene() => new Scene { name = ActiveSceneName };
    }
}
