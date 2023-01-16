using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace sutheim.NativeLibraryManager
{
    [CreateAssetMenu(fileName = "NativeLibrarySettings", menuName = "NativeLibraryManager/Settings", order = 1)]
    public class NativeLibraryManagerSettings : ScriptableObject
    {
        public List<string> AssembliesToCheck = new List<string>{"Assembly-CSharp"};
        public List<string> Paths = new List<string>{"Plugins"};
    }
}
