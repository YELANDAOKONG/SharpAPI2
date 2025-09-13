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

    // 缓存类名格式（使用'/'作为分隔符）
    private readonly Dictionary<string, string> _normalizedClassNames = new Dictionary<string, string>();

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
            if (classData is null || classData.Length == 0)
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

            if (clazz is null)
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
                
                // 查找方法的 Code 属性
                var codeAttribute = method.Attributes.FirstOrDefault(attr => attr.Name == "Code");
                if (codeAttribute is null)
                {
                    Logger?.Debug($"Method {methodName} has no Code attribute, skipping");
                    continue;
                }

                AttributeInfoStruct info = new AttributeInfoStruct()
                {
                    AttributeLength = 0, // Ignored
                    AttributeNameIndex = 0, // Ignored
                    Info = codeAttribute.Info,
                };
                
                try
                {
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
                    codeAttribute.Info = code.ToBytesWithoutIndexAndLength();
                }
                catch (Exception ex)
                {
                    Logger?.Error($"Error processing Code attribute for method {methodName}: {ex}");
                    // 继续处理其他方法
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
    /// 规范化类名格式（使用'/'作为分隔符）
    /// </summary>
    private string NormalizeClassName(string className)
    {
        if (_normalizedClassNames.TryGetValue(className, out var normalized))
        {
            return normalized;
        }
        
        normalized = className.Replace('.', '/');
        _normalizedClassNames[className] = normalized;
        return normalized;
    }

    /// <summary>
    /// 检查是否有针对指定类的 Mixin 方法
    /// </summary>
    private bool HasMixinForClass(string className)
    {
        string normalizedClassName = NormalizeClassName(className);
        
        return MixinMethods.Any(mixin =>
        {
            string targetClassName = mixin.Attribute.ClassName;
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
            
            // 根据 NameType 处理类名匹配
            switch (mixin.Attribute.NameType)
            {
                case NameType.Default:
                    return normalizedTargetClassName == normalizedClassName;
                case NameType.ObfuscatedName:
                    return Searcher.MatchClass(normalizedClassName, normalizedTargetClassName);
                case NameType.MappedName:
                    var classMapping = Searcher.Set.Classes.Values
                        .FirstOrDefault(c => NormalizeClassName(c.MappedName) == normalizedTargetClassName);
                    return classMapping != null && NormalizeClassName(classMapping.ObfuscatedName) == normalizedClassName;
                default:
                    return false;
            }
        });
    }

    private List<MixinMethodInfo> FindMatchingMixins(string className, string methodName, string methodDescriptor)
    {
        string normalizedClassName = NormalizeClassName(className);
        var result = new List<MixinMethodInfo>();
        
        foreach (var mixin in MixinMethods)
        {
            string targetClassName = mixin.Attribute.ClassName;
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
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
                    classNameMatches = normalizedTargetClassName == normalizedClassName;
                    methodNameMatches = targetMethodName == methodName;
                    break;
                case NameType.ObfuscatedName:
                    // 使用 MappingSearcher 检查混淆名是否匹配
                    classNameMatches = Searcher.MatchClass(normalizedClassName, normalizedTargetClassName);
                    methodNameMatches = targetMethodName == methodName;
                    break;
                case NameType.MappedName:
                    // 使用 MappingSearcher 获取映射名并比较
                    var classMapping = Searcher.Set.Classes.Values
                        .FirstOrDefault(c => NormalizeClassName(c.MappedName) == normalizedTargetClassName);
                    
                    if (classMapping != null)
                    {
                        classNameMatches = NormalizeClassName(classMapping.ObfuscatedName) == normalizedClassName;
                        
                        // 对于方法名，可能需要查找映射
                        var methodMapping = classMapping.Methods
                            .FirstOrDefault(m => NormalizeClassName(m.MappedName) == targetMethodName);
                        
                        if (methodMapping != null)
                        {
                            methodNameMatches = methodMapping.ObfuscatedName == methodName;
                        }
                        else
                        {
                            // 如果没有找到映射，直接比较
                            methodNameMatches = targetMethodName == methodName;
                        }
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
