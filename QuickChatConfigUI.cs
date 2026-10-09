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

    /// <summary>宿主注入的模块实例。配置页靠它问「框架里有哪些角色、哪些会话被删了」。</summary>
    QuickChatModule? Runtime => Module as QuickChatModule;

    // 「探测」结果缓存一次，供本次渲染的所有控件复用。
    // 这三个都要读磁盘或运行时状态，不能让每个按钮各查一遍。
    IReadOnlyList<string> allCharacters = Array.Empty<string>();
    IReadOnlyList<string> activeCharacters = Array.Empty<string>();

    const string InputStyle = "width:100%; min-height:32px; padding:5px 10px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:transparent; color:inherit; color-scheme:light dark; box-sizing:border-box;";
    const string TextAreaStyle = "width:100%; min-height:86px; padding:8px 10px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:transparent; color:inherit; color-scheme:light dark; box-sizing:border-box; resize:vertical; font-family:inherit; line-height:1.45;";
    const string ColorPickerStyle = "width:46px; height:32px; padding:2px; border:1px solid rgba(128,128,128,.35); border-radius:6px; background:#fff; cursor:pointer; flex:0 0 auto;";
    const string ButtonStyle = "padding:7px 16px; border-radius:6px; border:1px solid rgba(128,128,128,.35); background:transparent; color:inherit; cursor:pointer; font-weight:650; font-size:13px;";

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

        RefreshProbe();

        PropertyInfo[] properties = typeof(QuickChatConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(item => item.CanRead && item.CanWrite)
            // 群聊列表需要专门的编辑器（增删行、两个字段），不走通用控件。
            .Where(item => item.Name != nameof(QuickChatConfig.Groups))
            // 「流式输出」置顶（排在「启用群聊」上面），接着是群聊那两项。
            .OrderBy(item => item.Name == nameof(QuickChatConfig.StreamingOutput) ? 0
                : item.Name == nameof(QuickChatConfig.EnableGroupChat) ? 1
                : item.Name == nameof(QuickChatConfig.PublicLobbyName) ? 2 : 3)
            .ToArray();

        int sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "style", "display:flex; flex-direction:column; gap:18px;");

        // 打开窗口：配置页在 Alife 的模块设置里，窗口是 Electron 的独立悬浮窗，
        // 给一个按钮，省得每次都要记全局快捷键。
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "style", "display:flex; align-items:center; gap:12px; flex-wrap:wrap; padding-bottom:14px; border-bottom:1px solid rgba(128,128,128,.18);");
        builder.OpenElement(sequence++, "button");
        builder.AddAttribute(sequence++, "type", "button");
        builder.AddAttribute(sequence++, "style", ButtonStyle);
        builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, OpenWindow));
        builder.AddContent(sequence++, "打开快聊窗口");
        builder.CloseElement();
        builder.OpenElement(sequence++, "span");
        builder.AddAttribute(sequence++, "style", "font-size:12px; line-height:1.5; color:rgba(128,128,128,.85);");
        builder.AddContent(sequence++, "在鼠标附近弹出；也可以直接用上面的全局快捷键。");
        builder.CloseElement();
        builder.CloseElement();

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
            if (property.Name == nameof(QuickChatConfig.EnableGroupChat))
                sequence = RenderGroups(builder, sequence, config);
        }

        builder.CloseElement();
    }

    /// <summary>
    /// 群聊列表编辑器。公共大厅是隐式存在的（名字取「公共大厅名称」），
    /// 这里只编辑额外的小群，所以不需要「是否公共」这种开关。
    /// </summary>
    string managementNote = "";
    string? pendingDelete;
    string newGroupName = "";
    int RenderGroups(RenderTreeBuilder builder, int sequence, QuickChatConfig config)
    {
        int seq = sequence;
        builder.OpenElement(seq++, "details");
        builder.AddAttribute(seq++, "style", "padding:14px;border:1px solid rgba(128,128,128,.3);border-radius:8px;");
        builder.OpenElement(seq++, "summary");
        builder.AddAttribute(seq++, "style", "cursor:pointer;font-weight:650;padding:4px 0;");
        builder.AddContent(seq++, "小群管理（点击展开）");
        builder.CloseElement();
        builder.OpenElement(seq++, "div");
        builder.AddContent(seq++, "你始终拥有管理权限；建群角色可以拉人、清退和限制群成员，不能清退或限制你。勾选成员可拉人，取消勾选可清退；限制回复会停止该角色在此群的发言。");
        builder.CloseElement();
        builder.OpenElement(seq++, "button");
        builder.AddAttribute(seq++, "type", "button");
        builder.AddAttribute(seq++, "style", ButtonStyle);
        builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => InvokeAsync(StateHasChanged)));
        builder.AddContent(seq++, "刷新小群列表");
        builder.CloseElement();
        foreach (QuickChatConversation group in Runtime?.GetSmallGroups() ?? Array.Empty<QuickChatConversation>())
        {
            builder.OpenElement(seq++, "details");
            builder.SetKey(group.Id);
            builder.AddAttribute(seq++, "style", "margin:10px 0;padding:10px;border:1px solid rgba(128,128,128,.25);border-radius:6px;");
            builder.OpenElement(seq++, "summary");
            builder.AddAttribute(seq++, "style", "cursor:pointer;font-weight:650;padding:4px 0;");
            builder.AddContent(seq++, group.Title + " · 建群者：" + QuickChatPrincipal.Parse(group.CreatorId).Name);
            builder.CloseElement();
            builder.AddContent(seq++, "主人（始终保留，拥有管理权限）");
            foreach (string name in allCharacters.Concat(group.Members).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string memberName = name;
                bool included = group.Members.Contains(name, StringComparer.OrdinalIgnoreCase);
                builder.OpenElement(seq++, "div");
                builder.AddAttribute(seq++, "style", "display:flex;gap:16px;align-items:center;");
                builder.OpenElement(seq++, "label");
                builder.OpenElement(seq++, "input");
                builder.AddAttribute(seq++, "type", "checkbox");
                builder.AddAttribute(seq++, "checked", included);
                builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
                    args => ChangeMember(group, memberName, args.Value is true, false)));
                builder.CloseElement();
                builder.AddContent(seq++, memberName);
                builder.CloseElement();
                builder.OpenElement(seq++, "label");
                builder.OpenElement(seq++, "input");
                builder.AddAttribute(seq++, "type", "checkbox");
                builder.AddAttribute(seq++, "disabled", !included);
                builder.AddAttribute(seq++, "checked", group.MutedMembers.Contains(name, StringComparer.OrdinalIgnoreCase));
                builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
                    args => ChangeMember(group, memberName, args.Value is true, true)));
                builder.CloseElement();
                builder.AddContent(seq++, "限制回复");
                builder.CloseElement();
                builder.CloseElement();
            }
            builder.OpenElement(seq++, "button");
            builder.AddAttribute(seq++, "type", "button");
            builder.AddAttribute(seq++, "style", ButtonStyle);
            builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () =>
            {
                if (pendingDelete != group.Id) pendingDelete = group.Id;
                else
                {
                    managementNote = Runtime?.DeleteSmallGroup(group.Id) ?? "快聊尚未运行";
                    if (managementNote.StartsWith("已删除"))
                        config.Groups?.RemoveAll(g => ("group:" + g.Name.Trim()).Equals(group.Id, StringComparison.OrdinalIgnoreCase));
                    pendingDelete = null;
                    CommitConfig();
                }
            }));
            builder.AddContent(seq++, pendingDelete == group.Id ? "确认删除小群及记录" : "删除小群");
            builder.CloseElement();
            if (pendingDelete == group.Id)
            {
                builder.OpenElement(seq++, "button");
                builder.AddAttribute(seq++, "type", "button");
                builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => pendingDelete = null));
                builder.AddContent(seq++, "取消");
                builder.CloseElement();
            }
            builder.CloseElement();
        }
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "style", InputStyle);
        builder.AddAttribute(seq++, "placeholder", "新小群名称");
        builder.AddAttribute(seq++, "value", newGroupName);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, args => newGroupName = args.Value?.ToString() ?? ""));
        builder.CloseElement();
        builder.OpenElement(seq++, "button");
        builder.AddAttribute(seq++, "type", "button");
        builder.AddAttribute(seq++, "style", ButtonStyle);
        builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () =>
        {
            managementNote = Runtime?.CreateSmallGroup(newGroupName) ?? "快聊尚未运行";
            if (managementNote.StartsWith("已创建")) newGroupName = "";
        }));
        builder.AddContent(seq++, "新增小群（先保留主人，再勾选成员）");
        builder.CloseElement();
        builder.AddContent(seq++, managementNote);
        builder.CloseElement();
        return seq;
    }

    void ChangeMember(QuickChatConversation group, string name, bool enabled, bool restriction)
    {
        var members = group.Members.ToList();
        var muted = group.MutedMembers.ToList();
        var target = restriction ? muted : members;
        target.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (enabled) target.Add(name);
        managementNote = Runtime?.UpdateSmallGroup(group.Id, members, muted) ?? "快聊尚未运行";
    }

    /// <summary>
    /// 「会话是怎么来的」说明区。
    /// <para>
    /// 这里原来放的是「已删除的会话 + 恢复全部」。那套机制（一份持久名单挡住同步逻辑）
    /// 已经删掉了：受管会话（角色的私聊、公共大厅、配置里的群）的存在性只应该由
    /// 「角色是否激活 / 配置里有没有这个群」决定，用名单挡的结果是删掉私聊的角色
    /// 重新激活也回不来，而且群聊成员会跟着少人。
    /// </para>
    /// <para>
    /// 现在快聊窗口左下角的「删除」对受管会话退化成「清空记录」，对自建会话才真删，
    /// 所以不再需要恢复入口。这一段留下是为了让用户知道会话是怎么来的。
    /// </para>
    /// </summary>
    int RenderConversationRules(RenderTreeBuilder builder, int sequence)
    {
        int seq = sequence;

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "display:flex; flex-direction:column; gap:6px; padding-bottom:14px; border-bottom:1px solid rgba(128,128,128,.18);");

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "font-weight:650;");
        builder.AddContent(seq++, "会话是怎么来的");
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "font-size:12px; line-height:1.5; color:rgba(128,128,128,.85);");
        builder.AddContent(seq++, "每个已激活的角色都有一条私聊会话（还没聊过就是空的），"
            + "加上公共大厅、下面配置的小群。所以「有没有会话」只取决于角色有没有激活、"
            + "群有没有配在这里 —— 停用一个角色，它的私聊会话才会消失。");
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "font-size:12px; line-height:1.5; color:rgba(128,128,128,.85);");
        builder.AddContent(seq++, "快聊窗口左下角的「删除」：对上面的会话是清空聊天记录（会话保留），"
            + "对你在窗口里「新建」出来的会话才是真删。");
        builder.CloseElement();

        builder.CloseElement();
        return seq;
    }

    /// <summary>
    /// 刷新「框架里有哪些角色 / 哪些已激活」。
    /// 宿主还没注入 Module 时全部退化成空列表，不让配置页崩。
    /// </summary>
    void RefreshProbe()
    {
        try
        {
            allCharacters = Runtime?.GetAllCharacterNames() ?? Array.Empty<string>();
            activeCharacters = Runtime?.GetActiveCharacterNames() ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            allCharacters = Array.Empty<string>();
            activeCharacters = Array.Empty<string>();
        }
    }

    /// <summary>用探测到的角色新建一个群，成员直接填满，省得一个个手敲（敲错就是拉进幽灵成员）。</summary>
    void AddGroupWithCharacters(bool onlyActive)
    {
        if (Configuration is not QuickChatConfig config)
            return;

        IReadOnlyList<string> names = onlyActive ? activeCharacters : allCharacters;
        if (names.Count == 0)
            return;

        string label = onlyActive ? "激活角色" : "全部角色";
        config.Groups.Add(new QuickChatGroupConfig
        {
            Name = $"{label}群 {config.Groups.Count + 1}",
            Members = string.Join(",", names)
        });
        CommitConfig();
    }

    void OpenWindow()
    {
        if (Module is QuickChatModule module)
            _ = module.OpenWindowAsync();
    }

    void UpdateGroup(int index, Action<QuickChatGroupConfig> mutate)
    {
        if (Configuration is not QuickChatConfig config || index < 0 || index >= config.Groups.Count)
            return;

        mutate(config.Groups[index]);
        CommitConfig();
    }

    void AddGroup()
    {
        if (Configuration is not QuickChatConfig config)
            return;

        config.Groups.Add(new QuickChatGroupConfig { Name = $"小群 {config.Groups.Count + 1}" });
        CommitConfig();
    }

    void RemoveGroup(int index)
    {
        if (Configuration is not QuickChatConfig config || index < 0 || index >= config.Groups.Count)
            return;

        config.Groups.RemoveAt(index);
        CommitConfig();
    }

    void CommitConfig()
    {
        if (Configuration is not QuickChatConfig config)
            return;

        if (Module is IConfigurable configurable)
            configurable.Configuration = config;

        InvokeAsync(StateHasChanged);
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




