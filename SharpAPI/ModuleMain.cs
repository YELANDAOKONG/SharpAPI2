using SharpLoader.Core.Modding;
using SharpLoader.Modding;
using SharpLoader.Utilities;

namespace SharpAPI;

public class ModuleMain : ModuleBase
{
    public static ModuleMain? Instance { get; private set; }
    
    public ModuleMain()
    {
        Instance = this;
    }

    protected override void OnInitialize(ModuleManager manager, LoggerService? logger)
    {
        Logger?.Info("SharpAPI loaded.");
    }
    
    public override byte[]? ModifyClass(string className, byte[]? classData)
    {
        return null;
    }
}