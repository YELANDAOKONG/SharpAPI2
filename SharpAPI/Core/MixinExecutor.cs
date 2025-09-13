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
    
    public List<MixinInfo> Mixins { get; private set; }

    // 缓存类名格式（使用'/'作为分隔符）
    private readonly Dictionary<string, string> _normalizedClassNames = new Dictionary<string, string>();

    public MixinExecutor(ModuleManager manager, LoggerService? logger)
    {
        Manager = manager;
        Searcher = manager.MappingSearcher;
        Logger = logger;
        
        Logger?.Info("Mixin Executor Initialized.");
        Mixins = MixinScanner.ScanAllLoadedAssemblies();
        Logger?.Info($"Found {Mixins.Count} mixins.");
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
                // 检查是否有针对此类的 Mixin
                bool hasMixinForClass = HasMixinForClass(className);
                // Logger?.Debug($"Class {className} inquiry: {(hasMixinForClass ? "will modify" : "skip")}");
                
                // 返回空数组表示需要修改此类，NULL表示不需要
                return hasMixinForClass ? Array.Empty<byte>() : null;
            }

            // 检查是否有针对此类的 Mixin，如果没有则直接返回 NULL
            if (!HasMixinForClass(className))
            {
                return null;
            }

            Logger?.Info($"Modifying class: {className} ({classData.Length} Bytes)");

            var klass = ClassParser.Parse(classData);
            Class? clazz = Class.FromStruct(klass);

            if (clazz is null)
            {
                Logger?.Warn($"Failed to parse class: {className}");
                return null;
            }

            bool modified = false;

            // 首先应用类级别的 Mixin
            var classMixins = FindClassMixins(className);
            foreach (var mixin in classMixins)
            {
                try
                {
                    clazz = ((ClassMixinInfo)mixin).Invoke(clazz);
                    modified = true;
                    Logger?.Debug($"Applied class mixin: {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}");
                }
                catch (Exception ex)
                {
                    Logger?.Error($"Error executing class mixin method {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}: {ex}");
                }
            }

            // 然后应用字段级别的 Mixin
            foreach (var field in clazz.Fields.ToList())
            {
                var fieldMixins = FindFieldMixins(className, field.Name, field.Descriptor);
                if (!fieldMixins.Any())
                {
                    continue;
                }

                Logger?.Debug($"Applying {fieldMixins.Count} field mixin(s) to field: {field.Name}");
                
                Field currentField = field;
                foreach (var mixin in fieldMixins)
                {
                    try
                    {
                        currentField = ((FieldMixinInfo)mixin).Invoke(clazz, currentField);
                        modified = true;
                        Logger?.Debug($"Applied field mixin: {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}");
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error($"Error executing field mixin method {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}: {ex}");
                    }
                }

                // 替换原始字段
                int fieldIndex = clazz.Fields.IndexOf(field);
                if (fieldIndex >= 0)
                {
                    clazz.Fields[fieldIndex] = currentField;
                }
            }

            // 然后应用方法级别的 Mixin
            foreach (var method in clazz.Methods.ToList())
            {
                // 首先应用完整方法级别的 Mixin
                var methodMixins = FindMethodMixins(className, method.Name, method.Descriptor);
                if (methodMixins.Any())
                {
                    Logger?.Debug($"Applying {methodMixins.Count} method mixin(s) to method: {method.Name}");
                    
                    Method currentMethod = method;
                    foreach (var mixin in methodMixins)
                    {
                        try
                        {
                            currentMethod = ((MethodMixinInfo)mixin).Invoke(clazz, currentMethod);
                            modified = true;
                            Logger?.Debug($"Applied method mixin: {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logger?.Error($"Error executing method mixin method {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}: {ex}");
                        }
                    }

                    // 替换原始方法
                    int methodIndex = clazz.Methods.IndexOf(method);
                    if (methodIndex >= 0)
                    {
                        clazz.Methods[methodIndex] = currentMethod;
                    }
                }

                // 然后应用方法代码级别的 Mixin
                var methodCodeMixins = FindMethodCodeMixins(className, method.Name, method.Descriptor);
                if (!methodCodeMixins.Any())
                {
                    continue;
                }

                Logger?.Debug($"Applying {methodCodeMixins.Count} method code mixin(s) to method: {method.Name}");
                
                // 查找方法的 Code 属性
                var codeAttribute = method.Attributes.FirstOrDefault(attr => attr.Name == "Code");
                if (codeAttribute is null)
                {
                    Logger?.Debug($"Method {method.Name} has no Code attribute, skipping");
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
                    CodeAttributeStruct currentCode = code;
    
                    foreach (var mixin in methodCodeMixins)
                    {
                        try
                        {
                            // 直接传入 CodeAttributeStruct 以允许修改更多内容
                            currentCode = ((MethodCodeMixinInfo)mixin).Invoke(clazz, currentCode);
                            modified = true;
                            Logger?.Debug($"Applied method code mixin: {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logger?.Error($"Error executing method code mixin method {mixin.Method.DeclaringType?.Name}.{mixin.Method.Name}: {ex}");
                        }
                    }
    
                    codeAttribute.Info = currentCode.ToBytesWithoutIndexAndLength();
                }
                catch (Exception ex)
                {
                    Logger?.Error($"Error processing Code attribute for method {method.Name}: {ex}");
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
    /// 检查是否有针对指定类的 Mixin
    /// </summary>
    private bool HasMixinForClass(string className)
    {
        string normalizedClassName = NormalizeClassName(className);
        
        return Mixins.Any(mixin =>
        {
            string targetClassName = GetTargetClassName(mixin);
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
            
            // 根据 NameType 处理类名匹配
            return MatchClassName(normalizedClassName, normalizedTargetClassName, GetNameType(mixin));
        });
    }

    private List<MixinInfo> FindClassMixins(string className)
    {
        string normalizedClassName = NormalizeClassName(className);
        var result = new List<MixinInfo>();
        
        foreach (var mixin in Mixins.Where(m => m is ClassMixinInfo))
        {
            string targetClassName = GetTargetClassName(mixin);
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
            
            if (MatchClassName(normalizedClassName, normalizedTargetClassName, GetNameType(mixin)))
            {
                result.Add(mixin);
            }
        }
        return result.OrderBy(m => m.Priority).ToList();
    }

    private List<MixinInfo> FindFieldMixins(string className, string fieldName, string fieldDescriptor)
    {
        string normalizedClassName = NormalizeClassName(className);
        var result = new List<MixinInfo>();
        
        foreach (var mixin in Mixins.Where(m => m is FieldMixinInfo))
        {
            var fieldMixin = (FieldMixinInfo)mixin;
            string targetClassName = GetTargetClassName(mixin);
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
            
            if (MatchClassName(normalizedClassName, normalizedTargetClassName, GetNameType(mixin)) &&
                fieldMixin.Attribute.FieldName == fieldName &&
                fieldMixin.Attribute.FieldDescriptor == fieldDescriptor)
            {
                result.Add(mixin);
            }
        }
        return result.OrderBy(m => m.Priority).ToList();
    }

    private List<MixinInfo> FindMethodMixins(string className, string methodName, string methodDescriptor)
    {
        string normalizedClassName = NormalizeClassName(className);
        var result = new List<MixinInfo>();
        
        foreach (var mixin in Mixins.Where(m => m is MethodMixinInfo))
        {
            var methodMixin = (MethodMixinInfo)mixin;
            string targetClassName = GetTargetClassName(mixin);
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
            
            if (MatchClassName(normalizedClassName, normalizedTargetClassName, GetNameType(mixin)) &&
                methodMixin.Attribute.MethodName == methodName &&
                methodMixin.Attribute.MethodSignature == methodDescriptor)
            {
                result.Add(mixin);
            }
        }
        return result.OrderBy(m => m.Priority).ToList();
    }

    private List<MixinInfo> FindMethodCodeMixins(string className, string methodName, string methodDescriptor)
    {
        string normalizedClassName = NormalizeClassName(className);
        var result = new List<MixinInfo>();
        
        foreach (var mixin in Mixins.Where(m => m is MethodCodeMixinInfo))
        {
            var methodCodeMixin = (MethodCodeMixinInfo)mixin;
            string targetClassName = GetTargetClassName(mixin);
            string normalizedTargetClassName = NormalizeClassName(targetClassName);
            
            if (MatchClassName(normalizedClassName, normalizedTargetClassName, GetNameType(mixin)) &&
                methodCodeMixin.Attribute.MethodName == methodName &&
                methodCodeMixin.Attribute.MethodSignature == methodDescriptor)
            {
                result.Add(mixin);
            }
        }
        return result.OrderBy(m => m.Priority).ToList();
    }

    private string GetTargetClassName(MixinInfo mixin)
    {
        return mixin switch
        {
            ClassMixinInfo classMixin => classMixin.Attribute.ClassName,
            MethodMixinInfo methodMixin => methodMixin.Attribute.ClassName,
            MethodCodeMixinInfo methodCodeMixin => methodCodeMixin.Attribute.ClassName,
            FieldMixinInfo fieldMixin => fieldMixin.Attribute.ClassName,
            _ => throw new InvalidOperationException($"Unknown mixin type: {mixin.GetType()}")
        };
    }

    private NameType GetNameType(MixinInfo mixin)
    {
        return mixin switch
        {
            ClassMixinInfo classMixin => classMixin.Attribute.NameType,
            MethodMixinInfo methodMixin => methodMixin.Attribute.NameType,
            MethodCodeMixinInfo methodCodeMixin => methodCodeMixin.Attribute.NameType,
            FieldMixinInfo fieldMixin => fieldMixin.Attribute.NameType,
            _ => NameType.Default
        };
    }

    private bool MatchClassName(string actualClassName, string targetClassName, NameType nameType)
    {
        switch (nameType)
        {
            case NameType.Default:
                return targetClassName == actualClassName;
            case NameType.ObfuscatedName:
                return Searcher.MatchClass(actualClassName, targetClassName);
            case NameType.MappedName:
                var classMapping = Searcher.Set.Classes.Values
                    .FirstOrDefault(c => NormalizeClassName(c.MappedName) == targetClassName);
                return classMapping != null && NormalizeClassName(classMapping.ObfuscatedName) == actualClassName;
            default:
                return false;
        }
    }
    
    public void RescanMixins()
    {
        Mixins = MixinScanner.ScanAllLoadedAssemblies();
        Logger?.Info($"Rescanned mixins. Found {Mixins.Count} mixins.");
    }
}
