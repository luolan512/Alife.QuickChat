# Marisa.QuickChat

一个 Alife 4.x 插件，提供桌面快聊悬浮窗。通过全局快捷键在鼠标附近快速呼出对话框，直接与当前激活的桌宠对话。

## 功能

- 全局快捷键呼出/隐藏快聊窗口，默认 `Alt+Q`
- 支持多个已激活桌宠切换
- 展开聊天记录、最小化输入条、自适应高度
- Markdown 基础渲染：标题、粗体、斜体、删除线、代码、列表、引用、表格、链接、分割线
- 支持图片和文件：
  - 文件选择器选择的文件保留原始路径；
  - 复制/粘贴的图片保存到插件相对目录 `Temp/Images`；
  - 等待区显示缩略图，可点击查看原图；
  - 拖入消息区直接发送，拖入输入区加入等待区；
  - 发送给模型的是本机路径，由模型/插件侧按能力读取。
- 配色、透明度、窗口尺寸、消息数量等可配置
- 清理按钮带二次确认，只清理快聊显示，不影响原始聊天记录
- 插件会话启动时自动清理 `Temp/Images`

## 安装

1. 将整个 `Marisa.QuickChat` 目录复制到 Alife 的插件目录，例如：

   ```text
   Alife/Storage/Plugins/Marisa.QuickChat
   ```

2. 重启 Alife。
3. 在角色/模块配置中启用「快聊」模块。
4. 默认使用 `Alt+Q` 呼出窗口。

## 目录内容

```text
Marisa.QuickChat/
├─ README.md
├─ manifest.json
├─ VERSION.txt
├─ QuickChatModule.cs
├─ QuickChatRuntime.cs
├─ QuickChatWindow.cs
├─ QuickChatBridge.cs
├─ QuickChatConfig.cs
├─ QuickChatContentFilter.cs
└─ Resources/
   └─ QuickChat/
      ├─ index.html
      ├─ main.js
      └─ style.css
```

## 配置

在 Alife 模块配置中可调整：

- 全局快捷键
- 窗口宽高、自适应高度、最小高度
- 单次加载消息数量 / 显示全部消息
- Esc 隐藏窗口
- 消息来源标记
- 配色预设、各类底色与透明度
- 拖放提示遮罩的模糊强度

## 清理说明

- 清理按钮：清空当前快聊显示，需要二次确认。
- 原始聊天记录：清理按钮不会删除。
- `Temp/Images`：快聊新会话启动时自动清理，用于存放粘贴/复制的图片缓存和缩略图。

## 兼容性

- 目标客户端：Alife `4.x`
- 插件版本：`4.0.0`
- 平台：Windows
