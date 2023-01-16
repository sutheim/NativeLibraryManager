using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace sutheim.NativeLibraryManager
{
#if UNITY_EDITOR
    [InitializeOnLoadAttribute]
#endif
    public static class NativeLibraryManager
    {
        [DllImport("kernel32.dll")]
        static private extern uint GetLastError();

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static private extern IntPtr LoadLibrary(string libraryPath);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static private extern bool FreeLibrary(IntPtr libraryPointer);

        [DllImport("kernel32")]
        static private extern IntPtr GetProcAddress(IntPtr libraryPointer, string functionName);


        static internal Dictionary<string, LoadedLibrary> _loadedLibraries = new Dictionary<string, LoadedLibrary>();

        static internal NativeLibraryManagerSettings _settings;


        public struct LoadedLibrary
        {
            public IntPtr libraryPointer;
            public Dictionary<string, IntPtr> functions;

            public LoadedLibrary(IntPtr libraryPointer)
            {
                this.libraryPointer = libraryPointer;
                this.functions = new Dictionary<string, IntPtr>();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static private void Initialize()
        {
            _settings = Resources.Load<NativeLibraryManagerSettings>("NativeLibrarySettings");

            if(_settings==null)
            {
                throw new System.Exception("NativeLibraryManagerSettings could not be found. One should exist in NativeLibraryManager's Resources folder");
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName().Name;
                var shouldCheckAssembly = false;

                foreach (var filter in _settings.AssembliesToCheck)
                {
                    if(name.StartsWith(filter))
                    {
                        shouldCheckAssembly = true;
                        break;
                    }
                }

                if(!shouldCheckAssembly)
                {
                    continue;
                }

                foreach (var type in assembly.GetTypes()) {
                    IntPtr libraryPointer = IntPtr.Zero;
                    var libraryName = "";

                    var customAttributes = type.GetCustomAttributes(typeof(NativeLibrary), true);
                    if (customAttributes.Length == 1)
                    {
                        libraryName = (customAttributes[0] as NativeLibrary).libraryName;
                        libraryPointer = GetOrLoadLibrary(libraryName);
                    }

                    var allFields = type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    foreach (var field in allFields)
                    {
                        var customFieldAttributes = field.GetCustomAttributes(typeof(NativeLibraryFunction), true);
                        if (customFieldAttributes.Length == 0)
                        {
                            continue;
                        }

                        var fieldLibraryPointer = IntPtr.Zero;

                        // If the NativeLibraryFunction contains a library name use that instead of the outer type NativeLibrary library
                        var nativeLibraryFunctionData = customFieldAttributes[0] as NativeLibraryFunction;
                        if (!string.IsNullOrEmpty(nativeLibraryFunctionData.libraryName))
                        {
                            fieldLibraryPointer = GetOrLoadLibrary(nativeLibraryFunctionData.libraryName);
                        }
                        else
                        {
                            fieldLibraryPointer = libraryPointer;
                        }

                        // The NativeLibraryFunction contained no library name and there was no NativeLibrary on outer type. Any ways to statically verify this instead?
                        if (fieldLibraryPointer == IntPtr.Zero)
                        {
                            throw new NativeLibraryAttributeException($"NativeLibraryFunctions on field {field} includes no library name, and no NativeLibrary attribute found on owning type {type}");
                        }

                        var functionName = nativeLibraryFunctionData.functionName;

                        SetDelegateOnField(fieldLibraryPointer, field, functionName);
                    }
                }
            }
        }

        private static void SetDelegateOnField(IntPtr libraryPointer, FieldInfo field, string functionName)
        {
            IntPtr functionPointer = GetOrLoadFunction(libraryPointer, functionName);

            var functionDelegate = Marshal.GetDelegateForFunctionPointer(functionPointer, field.FieldType);
            field.SetValue(null, functionDelegate); // object parameter is ignored when setting value on a static field
        }

        private static IntPtr GetOrLoadFunction(IntPtr libraryPointer, string functionName)
        {
            string libraryName = "";
            foreach (var library in _loadedLibraries)
            {
                if(library.Value.libraryPointer == libraryPointer)
                {
                    libraryName = library.Key;
                    break;
                }
            }

            var functionPointer = GetProcAddress(libraryPointer, functionName);
            if (functionPointer == IntPtr.Zero)
            {
                throw new System.Exception($"Could not find function {functionName} in library {libraryName}. ErrorCode {GetLastError()}");
            }

            return functionPointer;
        }

        private static IntPtr GetOrLoadLibrary(string libraryName)
        {
            if(!_loadedLibraries.TryGetValue(libraryName, out var loadedLibrary))
            {
                string libraryPath = FindLibraryPathOnDisk(libraryName);
                IntPtr libraryPointer = LoadLibrary(libraryPath);
                if (libraryPointer == IntPtr.Zero)
                {
                    throw new System.Exception($"Could not load library {libraryPath}");
                }

                _loadedLibraries.Add(libraryName, new LoadedLibrary(libraryPointer));
                return libraryPointer;
            }

            return loadedLibrary.libraryPointer;
        }

        private static string FindLibraryPathOnDisk(string libraryName)
        {
            foreach (var path in _settings.Paths)
            {
                var combinedPath = Path.Combine(Application.dataPath, path, libraryName + ".dll");
                if(File.Exists(combinedPath))
                {
                    return combinedPath;
                }
            }

            throw new System.Exception($"Could not find library {libraryName}.dll in search paths");
        }

#if UNITY_EDITOR
        static NativeLibraryManager()
        {
            EditorApplication.playModeStateChanged += OnPlaymodeChanged;
        }

        static private void OnPlaymodeChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredEditMode:
                    foreach (var (name, library) in _loadedLibraries)
                    {
                        FreeLibrary(library.libraryPointer);
                    }
                    _loadedLibraries.Clear();
                    break;
                default:
                    break;
            }
        }
#endif
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class NativeLibraryFunction : System.Attribute
    {
        public string libraryName { get; private set; }
        public string functionName { get; private set; }

        public NativeLibraryFunction(string functionName) {
            this.functionName = functionName;
        }

        public NativeLibraryFunction(string libraryName, string functionName) {
            this.libraryName = libraryName;
            this.functionName = functionName;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class NativeLibrary : System.Attribute
    {
        public string libraryName { get; private set; }

        public NativeLibrary(string libraryName) {
            this.libraryName = libraryName;
        }
    }

    public class NativeLibraryAttributeException : Exception
    {
        public NativeLibraryAttributeException() {}
        public NativeLibraryAttributeException(string message) : base(message){}
        public NativeLibraryAttributeException(string message, Exception inner) : base(message, inner){}
    }
}
