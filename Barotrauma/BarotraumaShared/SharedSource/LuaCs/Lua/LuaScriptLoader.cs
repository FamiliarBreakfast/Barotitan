using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using System.Linq;

namespace Barotrauma
{
    class LuaScriptLoader : ScriptLoaderBase
    {

        public override object LoadFile(string file, Table globalContext)
        {
            if (!LuaCsFile.IsPathAllowedLuaException(file, false)) return null; 
                
            return File.ReadAllText(file);
        }

        public override bool ScriptFileExists(string file)
        {
            if (!LuaCsFile.IsPathAllowedLuaException(file, false)) return false;

            return File.Exists(file);
        }
    }
}
