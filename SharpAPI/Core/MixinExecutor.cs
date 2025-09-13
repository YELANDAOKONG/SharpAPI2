using System.Reflection;
using SharpLoader.Core.Minecraft.Mapping.Utilities;
using SharpLoader.Core.Modding;
using SharpLoader.Utilities;
using SharpMixin.Models;
using SharpMixin.Utilities;

namespace SharpAPI.Core;

public class MixinExecutor
{
    public ModuleManager Manager { get; private set; }
    public MappingSearcher Searcher { get; private set; }
    public LoggerService? Logger { get; private set; }
    
    public List<MixinMethodInfo> MixinMethods { get; private set; }

    public MixinExecutor(ModuleManager manager, LoggerService? logger)
    {
        Manager = manager;
        Searcher = manager.MappingSearcher;
        Logger = logger;
        
        Logger?.Info("Mixin Executor Initialized.");
        MixinMethods = MixinScanner.ScanAllLoadedAssemblies();
        Logger?.Info($"Found {MixinMethods.Count} mixin methods.");
    }
    

    public byte[]? ModifyClass(string className, byte[]? classData)
    {
        // TODO...
        return null;
    }
}