using System.Reflection;
using SharpASM.Models;
using SharpASM.Models.Code;
using SharpASM.Models.Struct;
using SharpASM.Models.Struct.Attribute;
using SharpASM.Parsers;
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
    
    /// <summary>
    /// 修改类的字节码
    /// </summary>
    /// <param name="className">类名</param>
    /// <param name="classData">类字节码，如果为null表示询问是否需要修改此类</param>
    /// <returns>
    /// - 如果classData为null：返回null表示不修改此类，返回空数组表示需要修改
    /// - 如果classData不为null：返回null表示不修改，返回字节数组表示修改后的类
    /// </returns>
    public byte[]? ModifyClass(string className, byte[]? classData)
    {
        try
        {
            // 处理询问是否需要修改此类的情况
            if (classData is null)
            {
                // 检查是否有针对此类的 Mixin 方法
                bool hasMixinForClass = HasMixinForClass(className);
                Logger?.Debug($"Class {className} inquiry: {(hasMixinForClass ? "will modify" : "skip")}");
                
                // 返回空数组表示需要修改此类，NULL表示不需要
                return hasMixinForClass ? Array.Empty<byte>() : null;
            }

            // 检查是否有针对此类的 Mixin 方法，如果没有则直接返回 NULL
            if (!HasMixinForClass(className))
            {
                return null;
            }

            Logger?.Info($"Modifying class: {className}");

            var klass = ClassParser.Parse(classData);
            Class? clazz = Class.FromStruct(klass);

            if (clazz is null || clazz == null)
            {
                Logger?.Warn($"Failed to parse class: {className}");
                return null;
            }

            bool modified = false;

            // 遍历类中的每个方法
            foreach (var method in clazz.Methods)
            {
                // 获取方法的原始名称和描述符
                string methodName = method.Name;
                string methodDescriptor = method.Descriptor;

                // 根据 MappingSearcher 解析映射名（如果需要）
                // 这里需要根据 Mixin 方法的 NameType 来处理名称匹配
                var matchingMixins = FindMatchingMixins(className, methodName, methodDescriptor);
                if (!matchingMixins.Any())
                {
                    continue;
                }

                Logger?.Debug($"Applying {matchingMixins.Count} mixin(s) to method: {methodName}");
                foreach (var methodAttribute in method.Attributes)
                {
                    if (methodAttribute.Name == "Code")
                    {
                        AttributeInfoStruct info = new AttributeInfoStruct()
                        {
                            AttributeLength = 0, // Ignored
                            AttributeNameIndex = 0, // Ignored
                            Info = methodAttribute.Info,
                        };
                        CodeAttributeStruct code = CodeAttributeStruct.FromStructInfo(info);

                        List<Code> codes = code.GetCode();
                        List<Code> currentCodes = codes;
                        foreach (var mixin in matchingMixins)
                        {
                            try
                            {
                                currentCodes = mixin.Invoke(clazz, currentCodes);
                                modified = true;
                                Logger?.Debug($"Applied mixin: {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}");
                            }
                            catch (Exception ex)
                            {
                                Logger?.Error($"Error executing mixin method {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}: {ex}");
                                Logger?.Trace($"Error executing mixin method {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}: {ex.StackTrace}");
                                // 继续处理其他 Mixin (不中断整个流程)
                            }
                        }
                        code.SetCode(currentCodes);
                        methodAttribute.Info = code.ToBytesWithoutIndexAndLength();
                    }
                    else
                    {
                        Logger?.Debug($"Skipping {methodName} (No Code Attribute)");
                    }
                }
            }

            if (!modified)
            {
                Logger?.Debug($"No modifications applied to class: {className}");
                return null;
            }

            ClassStruct mKlass = clazz.ToStruct();
            byte[]? modifiedBytecode = ClassParser.Serialize(mKlass);
            if (modifiedBytecode is null)
            {
                Logger?.Warn($"Failed to serialize modified class: {className}");
                return null;
            }

            Logger?.Info($"Successfully modified class: {className}");
            return modifiedBytecode;
        }
        catch (Exception ex)
        {
            Logger?.Error($"Unexpected error while modifying class {className}: {ex}");
            // 发生异常时返回 NULL，表示不修改此类，避免破坏原始类
            return null;
        }
    }

    /// <summary>
    /// 检查是否有针对指定类的 Mixin 方法
    /// </summary>
    private bool HasMixinForClass(string className)
    {
        return MixinMethods.Any(mixin =>
        {
            string targetClassName = mixin.Attribute.ClassName;
            
            // 根据 NameType 处理类名匹配
            switch (mixin.Attribute.NameType)
            {
                case NameType.Default:
                    return targetClassName == className;
                case NameType.ObfuscatedName:
                    return Searcher.MatchClass(className, targetClassName);
                case NameType.MappedName:
                    var classMapping = Searcher.Set.Classes.Values
                        .FirstOrDefault(c => c.MappedName == targetClassName);
                    return classMapping != null && classMapping.ObfuscatedName == className;
                default:
                    return false;
            }
        });
    }

    private List<MixinMethodInfo> FindMatchingMixins(string className, string methodName, string methodDescriptor)
    {
        var result = new List<MixinMethodInfo>();
        foreach (var mixin in MixinMethods)
        {
            string targetClassName = mixin.Attribute.ClassName;
            string targetMethodName = mixin.Attribute.MethodName;
            string targetMethodSignature = mixin.Attribute.MethodSignature;

            // 根据 NameType 处理类名和方法名的匹配
            bool classNameMatches = false;
            bool methodNameMatches = false;
            bool signatureMatches = targetMethodSignature == methodDescriptor;

            switch (mixin.Attribute.NameType)
            {
                case NameType.Default:
                    // 直接比较字符串
                    classNameMatches = targetClassName == className;
                    methodNameMatches = targetMethodName == methodName;
                    break;
                case NameType.ObfuscatedName:
                    // 使用 MappingSearcher 检查混淆名是否匹配
                    classNameMatches = Searcher.MatchClass(className, targetClassName);
                    methodNameMatches = targetMethodName == methodName; // 方法名通常直接比较，因为混淆名可能已经提供
                    break;
                case NameType.MappedName:
                    // 使用 MappingSearcher 获取映射名并比较
                    var classMapping = Searcher.Set.Classes.Values.FirstOrDefault(c => c.MappedName == targetClassName);
                    if (classMapping != null)
                    {
                        classNameMatches = classMapping.ObfuscatedName == className;
                    }
                    var methodMapping = Searcher.SearchMethodMapping(methodName);
                    if (methodMapping != null)
                    {
                        methodNameMatches = methodMapping.MappedName == targetMethodName;
                    }
                    break;
            }

            if (classNameMatches && methodNameMatches && signatureMatches)
            {
                result.Add(mixin);
            }
        }
        return result.OrderBy(m => m.Attribute.Priority).ToList();
    }
}
