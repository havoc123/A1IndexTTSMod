using System.Reflection;
using System.Runtime.InteropServices;
using JNGame.Ainpc.Llm;
using Newtonsoft.Json.Linq;
using Il2CppInterop.Runtime;

namespace A1IndexTTSMod;

/// <summary>Copies native IL2CPP options without reflection-boxing Nullable&lt;T&gt; proxies.</summary>
internal static class NativeLlmCallOptionsSnapshot
{
    public static object Create(object source, string responseFormatJson)
    {
        var stage = "type_check";
        try
        {
            if (source is not LlmCallOptions options)
                throw new System.ArgumentException("Source is not the game's LlmCallOptions type.", nameof(source));

            stage = "parse_response_format";
            var responseFormat = JObject.Parse(responseFormatJson);
            stage = "read_timeout";
            var timeout = options.TimeoutSeconds;
            stage = "construct_options";
            var clone = new LlmCallOptions { TimeoutSeconds = timeout };
            stage = "set_response_format";
            clone.ResponseFormat = responseFormat;

            // Generated Nullable<T> getters fail for the unboxed value wrappers in
            // this Unity build. Copy their native value bytes by the interop field
            // offsets and IL2CPP's reported value size; this preserves both unset and
            // configured HasValue/Value pairs without interpreting the wrapper.
            stage = "copy_temperature_native_field";
            CopyNativeValueField(options, clone, "Temperature");
            stage = "copy_max_tokens_native_field";
            CopyNativeValueField(options, clone, "MaxTokens");
            return clone;
        }
        catch (Exception exception)
        {
            while (exception is System.Reflection.TargetInvocationException invocation && invocation.InnerException is Exception inner) exception = inner;
            throw new InvalidOperationException(stage + ": " + exception.GetType().Name + ": " + exception.Message, exception);
        }
    }

    private static void CopyNativeValueField(LlmCallOptions source, LlmCallOptions destination, string name)
    {
        var nativeField = typeof(LlmCallOptions).GetField("NativeFieldInfoPtr_" + name,
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (nativeField is not IntPtr fieldPointer || fieldPointer == IntPtr.Zero)
            throw new InvalidOperationException("Native field metadata is missing for " + name + ".");
        var fieldType = IL2CPP.il2cpp_field_get_type(fieldPointer);
        var nativeClass = IL2CPP.il2cpp_class_from_type(fieldType);
        uint alignment = 0;
        var size = IL2CPP.il2cpp_class_value_size(nativeClass, ref alignment);
        if (size is <= 0 or > 32) throw new InvalidOperationException("Unexpected native value size for " + name + ": " + size + ".");
        var offset = checked((int)IL2CPP.il2cpp_field_get_offset(fieldPointer));
        var sourceAddress = IntPtr.Add(source.Pointer, offset);
        var destinationAddress = IntPtr.Add(destination.Pointer, offset);
        var bytes = new byte[size];
        Marshal.Copy(sourceAddress, bytes, 0, size);
        Marshal.Copy(bytes, 0, destinationAddress, size);
    }
}
