using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Marisa.QuickChat;

public sealed class QuickChatConfigUI : ComponentBase
{
    [Parameter]
    public Character? Character { get; set; }

    [Parameter]
    public ChatActivity? ChatActivity { get; set; }

    [Parameter]
    public object? Configuration { get; set; }

    [Parameter]
    public object? Module { get; set; }

    [Parameter]
    public RenderFragment? DefaultUI { get; set; }

    QuickChatConfig Config => Configuration as QuickChatConfig ?? new();

    const string InputStyle = "width:100%; min-height:32px; padding:5px 10px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:transparent; color:inherit; color-scheme:light dark; box-sizing:border-box;";
    const string TextAreaStyle = "width:100%; min-height:86px; padding:8px 10px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:transparent; color:inherit; color-scheme:light dark; box-sizing:border-box; resize:vertical; font-family:inherit; line-height:1.45;";
    const string ColorPickerStyle = "width:46px; height:32px; padding:2px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:#fff; cursor:pointer; flex:0 0 auto;";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Configuration is not QuickChatConfig config)
        {
            int errorSequence = 0;
            builder.OpenElement(errorSequence++, "div");
            builder.AddAttribute(errorSequence++, "style", "color:#ef4444; padding:16px; font-weight:600;");
            builder.AddContent(errorSequence++, "快聊配置加载失败。");
            builder.CloseElement();
            return;
        }

        PropertyInfo[] properties = typeof(QuickChatConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(item => item.CanRead && item.CanWrite)
            .ToArray();

        int sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "style", "display:flex; flex-direction:column; gap:18px;");

        foreach (PropertyInfo property in properties)
        {
            builder.OpenElement(sequence++, "div");
            builder.AddAttribute(sequence++, "style", "display:grid; grid-template-columns:minmax(150px, max-content) minmax(0,1fr); gap:8px 12px; align-items:center; padding-bottom:14px; border-bottom:1px solid rgba(128,128,128,.18);");

            builder.OpenElement(sequence++, "label");
            builder.AddAttribute(sequence++, "style", "font-weight:650; line-height:32px; overflow-wrap:anywhere;");
            builder.AddContent(sequence++, GetDisplayName(property));
            builder.CloseElement();

            builder.OpenElement(sequence++, "div");
            builder.AddAttribute(sequence++, "style", "min-width:0;");
            sequence = RenderControl(builder, sequence, property, property.GetValue(config));
            builder.CloseElement();

            string? description = GetDescription(property);
            if (string.IsNullOrWhiteSpace(description) == false)
            {
                builder.OpenElement(sequence++, "div");
                builder.AddAttribute(sequence++, "style", "grid-column:1 / -1; font-size:12px; line-height:1.5; color:rgba(128,128,128,.85); overflow-wrap:anywhere;");
                builder.AddContent(sequence++, description);
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        builder.CloseElement();
    }

    int RenderControl(RenderTreeBuilder builder, int sequence, PropertyInfo property, object? value)
    {
        int sequenceNumber = sequence;
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
        {
            builder.OpenElement(sequenceNumber++, "label");
            builder.AddAttribute(sequenceNumber++, "style", "display:inline-flex; align-items:center; gap:8px; min-height:32px; cursor:pointer; user-select:none;");
            builder.OpenElement(sequenceNumber++, "input");
            builder.AddAttribute(sequenceNumber++, "type", "checkbox");
            builder.AddAttribute(sequenceNumber++, "checked", Equals(value, true));
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, eventArgs.Value is bool flag ? flag : false)));
            builder.CloseElement();
            builder.AddContent(sequenceNumber++, Equals(value, true) ? "开启" : "关闭");
            builder.CloseElement();
            return sequenceNumber;
        }

        if (type == typeof(int))
        {
            builder.OpenElement(sequenceNumber++, "input");
            builder.AddAttribute(sequenceNumber++, "type", "number");
            builder.AddAttribute(sequenceNumber++, "step", "1");
            builder.AddAttribute(sequenceNumber++, "value", value?.ToString() ?? "0");
            builder.AddAttribute(sequenceNumber++, "style", InputStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, ParseInt(eventArgs.Value, (int)(value ?? 0)))));
            builder.CloseElement();
            return sequenceNumber;
        }

        if (type == typeof(double))
        {
            builder.OpenElement(sequenceNumber++, "input");
            builder.AddAttribute(sequenceNumber++, "type", "number");
            builder.AddAttribute(sequenceNumber++, "step", "0.01");
            builder.AddAttribute(sequenceNumber++, "value", Convert.ToDouble(value ?? 0.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.AddAttribute(sequenceNumber++, "style", InputStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, ParseDouble(eventArgs.Value, (double)(value ?? 0.0)))));
            builder.CloseElement();
            return sequenceNumber;
        }

        if (type.IsEnum)
        {
            string selected = Enum.GetName(type, value ?? Enum.GetValues(type).GetValue(0)) ?? "";
            builder.OpenElement(sequenceNumber++, "select");
            builder.AddAttribute(sequenceNumber++, "value", selected);
            builder.AddAttribute(sequenceNumber++, "style", InputStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, Enum.Parse(type, eventArgs.Value?.ToString() ?? selected))));

            foreach (string name in Enum.GetNames(type))
            {
                builder.OpenElement(sequenceNumber++, "option");
                builder.AddAttribute(sequenceNumber++, "value", name);
                builder.AddAttribute(sequenceNumber++, "selected", name == selected);
                builder.AddContent(sequenceNumber++, name);
                builder.CloseElement();
            }

            builder.CloseElement();
            return sequenceNumber;
        }

        if (type == typeof(List<string>))
        {
            IEnumerable<string> list = value as IEnumerable<string> ?? Enumerable.Empty<string>();
            string joined = property.Name == "Hotkeys"
                ? string.Join(", ", list)
                : string.Join(Environment.NewLine, list);

            if (property.Name == "Hotkeys")
            {
                builder.OpenElement(sequenceNumber++, "input");
                builder.AddAttribute(sequenceNumber++, "type", "text");
                builder.AddAttribute(sequenceNumber++, "value", joined);
                builder.AddAttribute(sequenceNumber++, "placeholder", "Alt+Q, Ctrl+Alt+Space");
                builder.AddAttribute(sequenceNumber++, "style", "width:100%; max-width:340px; min-height:32px; padding:5px 10px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:transparent; color:inherit; color-scheme:light dark; box-sizing:border-box;");
                builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                    this, eventArgs => SetValue(property, SplitHotkeys(eventArgs.Value?.ToString() ?? ""))));
                builder.CloseElement();
                return sequenceNumber;
            }

            builder.OpenElement(sequenceNumber++, "textarea");
            builder.AddAttribute(sequenceNumber++, "rows", 3);
            builder.AddAttribute(sequenceNumber++, "value", joined);
            builder.AddAttribute(sequenceNumber++, "style", TextAreaStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, SplitLines(eventArgs.Value?.ToString() ?? ""))));
            builder.CloseElement();
            return sequenceNumber;
        }

        if (type == typeof(string) && property.Name == "ColorPreset")
        {
            string selected = value as string ?? "自定义";
            string[] options = { "自定义", "深空", "墨黑", "雾白", "蓝色", "青色", "绿色", "紫色", "樱粉", "暖橙", "透明" };
            builder.OpenElement(sequenceNumber++, "select");
            builder.AddAttribute(sequenceNumber++, "value", selected);
            builder.AddAttribute(sequenceNumber++, "style", InputStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, eventArgs.Value?.ToString() ?? selected)));

            foreach (string name in options)
            {
                builder.OpenElement(sequenceNumber++, "option");
                builder.AddAttribute(sequenceNumber++, "value", name);
                builder.AddAttribute(sequenceNumber++, "selected", name == selected);
                builder.AddContent(sequenceNumber++, name);
                builder.CloseElement();
            }

            builder.CloseElement();
            return sequenceNumber;
        }

        if (type == typeof(string) && property.Name.EndsWith("Color", StringComparison.Ordinal))
        {
            string text = value as string ?? "";

            builder.OpenElement(sequenceNumber++, "div");
            builder.AddAttribute(sequenceNumber++, "style", "display:flex; align-items:center; gap:8px; min-width:0;");

            builder.OpenElement(sequenceNumber++, "input");
            builder.AddAttribute(sequenceNumber++, "type", "color");
            builder.AddAttribute(sequenceNumber++, "value", GetColorInputValue(text));
            builder.AddAttribute(sequenceNumber++, "style", ColorPickerStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, eventArgs.Value?.ToString() ?? text)));
            builder.CloseElement();

            builder.OpenElement(sequenceNumber++, "input");
            builder.AddAttribute(sequenceNumber++, "type", "text");
            builder.AddAttribute(sequenceNumber++, "value", text);
            builder.AddAttribute(sequenceNumber++, "style", "flex:1; min-width:0; " + InputStyle);
            builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, eventArgs => SetValue(property, eventArgs.Value?.ToString() ?? text)));
            builder.CloseElement();

            builder.CloseElement();
            return sequenceNumber;
        }

        builder.OpenElement(sequenceNumber++, "input");
        builder.AddAttribute(sequenceNumber++, "type", "text");
        builder.AddAttribute(sequenceNumber++, "value", value?.ToString() ?? "");
        builder.AddAttribute(sequenceNumber++, "style", InputStyle);
        builder.AddAttribute(sequenceNumber++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
            this, eventArgs => SetValue(property, eventArgs.Value?.ToString() ?? "")));
        builder.CloseElement();
        return sequenceNumber;
    }
    void SetValue(PropertyInfo property, object? value)
    {
        if (Configuration is not QuickChatConfig config)
            return;

        property.SetValue(config, value);

        if (Module is IConfigurable configurable)
            configurable.Configuration = config;

        InvokeAsync(StateHasChanged);
    }

    static string GetDisplayName(PropertyInfo property)
    {
        return property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? property.Name;
    }

    static string? GetDescription(PropertyInfo property)
    {
        return property.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }

    static List<string> SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static List<string> SplitHotkeys(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Split(new[] { '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => string.IsNullOrWhiteSpace(item) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    static int ParseInt(object? value, int fallback)
    {
        return int.TryParse(value?.ToString(), out int result) ? result : fallback;
    }

    static double ParseDouble(object? value, double fallback)
    {
        return double.TryParse(value?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out double result)
            ? result
            : fallback;
    }

    static string GetColorInputValue(string? value)
    {
        string text = value?.Trim() ?? "";
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$"))
                return text.ToLowerInvariant();

            if (System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9a-fA-F]{3}$"))
                return "#" + string.Concat(text.Skip(1).Select(item => $"{item}{item}")).ToLowerInvariant();

            return "#000000";
        }

        return text.ToLowerInvariant() switch
        {
            "黑" or "black" => "#000000",
            "白" or "white" => "#ffffff",
            "灰" or "gray" => "#6b7280",
            "红" or "red" => "#ef4444",
            "蓝" or "blue" => "#2f6fed",
            "绿" or "green" => "#22a06b",
            "黄" or "yellow" => "#facc15",
            "紫" or "purple" => "#8b5cf6",
            "粉" or "pink" => "#ff7eb6",
            "橙" or "orange" => "#ff9f43",
            "青" or "cyan" => "#22d3ee",
            _ => "#000000"
        };
    }
}




