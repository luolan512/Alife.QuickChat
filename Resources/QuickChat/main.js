const { ipcRenderer, shell, webUtils } = require("electron");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { pathToFileURL } = require("url");

const channel = new URLSearchParams(location.search).get("channel");
const app = document.getElementById("app");
const sidebar = document.getElementById("sidebar");
const sidebarRail = document.getElementById("sidebarRail");
const sidebarPanel = document.getElementById("sidebarPanel");
const railDots = document.getElementById("railDots");
const recordReveal = document.getElementById("recordReveal");
const bookmarkBar = document.getElementById("bookmarkBar");
const sessionRecords = document.getElementById("sessionRecords");
const recordHistory = document.getElementById("recordHistory");
const recordCurrent = document.getElementById("recordCurrent");
const recordActions = document.getElementById("recordActions");
const conversationTitle = document.getElementById("conversationTitle");
const titlebar = document.getElementById("titlebar");
const composer = document.getElementById("composer");
const minimizeButton = document.getElementById("minimizeButton");
const clearButton = document.getElementById("clearButton");
const confirmOverlay = document.getElementById("confirmOverlay");
const confirmOk = document.getElementById("confirmOk");
const confirmCancel = document.getElementById("confirmCancel");
const confirmTitle = document.getElementById("confirmTitle");
const confirmText = document.getElementById("confirmText");
const confirmPurge = document.getElementById("confirmPurge");
const confirmPurgeLabel = document.getElementById("confirmPurgeLabel");
const confirmHint = document.getElementById("confirmHint");
const compactPetName = document.getElementById("compactPetName");
const messageList = document.getElementById("messageList");
const input = document.getElementById("input");
const sendButton = document.getElementById("sendButton");
const restoreButton = document.getElementById("restoreButton");
const attachButton = document.getElementById("attachButton");
const imageInput = document.getElementById("imageInput");
const imagePreview = document.getElementById("imagePreview");
const screenshotButton = document.getElementById("screenshotButton");
const regionScreenshotButton = document.getElementById("regionScreenshotButton");
let screenshotBusy = false;

const state = {
  // 会话列表：私聊（kind="dm"，id=角色名）+ 群聊（kind="group"，id="group:群名"）。
  conversations: [],
  // 书签栏只放群聊（含公共大厅）。
  groups: [],
  selectedId: null,
  groupMode: false,
  enableGroupChat: true,
  // 鼠标扫过轨道是否自动弹开侧栏。默认关闭（键名与老版本的 sidebarHoverExpand 不同，
  // 是故意换的：老配置里存着 true，沿用同名会让「默认关闭」对老用户失效）。
  sidebarExpandOnHover: false,
  // 用户上次把侧栏留在「展开」还是「收起」。只在页面加载后的第一条 state 上还原一次。
  sidebarExpanded: false,
  groupWindowWidth: 620,
  groupWindowHeight: 560,
  maxVisibleMessages: 6,
  loadedMessageCount: 6,
  showAllMessages: false,
  loadingOlderMessages: false,
  hideOnEscape: true,
  compact: false,
  autoHeight: true,
  minWindowHeight: 96,
  maxWindowHeight: 460,
  manualSizeOverride: false,
  // 「清理」弹窗里那个「同时删除原始记录消息」勾选框的默认值，由配置页决定（默认开）。
  clearIncludesOriginal: true
};

// 确认弹窗当前要执行的动作。清理和删除会话共用同一个弹窗，靠它区分。
let confirmHandler = null;

const messagesByConversation = new Map();

// 每个会话「磁盘里还有没有更早的消息」。由后端的 history / history-prepend 带过来，
// 只有它为 true 时才允许向上翻页，避免滑到顶部反复空转。
const hasMoreHistory = new Map();

let lastStateSignature = "";
let lastConversationSignature = "";
let lastMessageSignature = "";
let lastComposerSignature = "";

let manualResizeEdge = null;
let resizeDebounceTimer = null;
let lastSentResizeSignature = "";
let lastInputHeight = 0;
let pendingResizeMove = null;
let resizeMoveScheduled = false;


const pendingImages = [];
const quickChatImageDirectory = path.join(__dirname, "..", "..", "Temp", "Images");

// 快聊记录不持久化，重启后旧临时文件没有任何引用者，启动时直接清空。
// 语义 = "Alife 退出/重启清理"（默认开）。窗口每次会话只创建一次，因此每次会话只清一遍。
function cleanupTemporaryImages() {
  try {
    if (fs.existsSync(quickChatImageDirectory) === false)
      return;
    for (const entry of fs.readdirSync(quickChatImageDirectory)) {
      try {
        fs.unlinkSync(path.join(quickChatImageDirectory, entry));
      } catch {
        // 单个文件删除失败不影响其余清理。
      }
    }
  } catch {
    // 清理失败不影响启动。
  }
}
cleanupTemporaryImages();

function sendResizeMove() {
  if (manualResizeEdge == null)
    return;

  sendMessage("resize-move");
}


function sendMessage(type, payload = {}) {
  if (!channel)
    return;
  ipcRenderer.send(channel, JSON.stringify({ type, ...payload }));
}

function hashText(text) {
  let hash = 0;
  const value = String(text || "");
  for (let index = 0; index < value.length; index++)
    hash = ((hash * 31) + value.charCodeAt(index)) | 0;
  return Math.abs(hash);
}

function colorFor(text) {
  return `hsl(${hashText(text) % 360}, 68%, 71%)`;
}

function clampNumber(value, min, max, fallback) {
  const number = Number(value);
  if (!Number.isFinite(number))
    return fallback;
  return Math.min(max, Math.max(min, number));
}

function hexToRgba(color, opacity, fallback = "rgba(0, 0, 0, 0)") {
  let value = String(color ?? "").trim();
  if (!value.startsWith("#"))
    value = `#${value}`;
  if (/^#[0-9a-f]{3}$/i.test(value))
    value = `#${value[1]}${value[1]}${value[2]}${value[2]}${value[3]}${value[3]}`;
  if (!/^#[0-9a-f]{6}$/i.test(value))
    return fallback;

  const red = parseInt(value.slice(1, 3), 16);
  const green = parseInt(value.slice(3, 5), 16);
  const blue = parseInt(value.slice(5, 7), 16);
  return `rgba(${red}, ${green}, ${blue}, ${clampNumber(opacity, 0, 1, 1)})`;
}

function applyTheme(theme = {}) {
  const root = document.documentElement.style;
  const panelColor = theme.panelColor;
  const panelOpacity = clampNumber(theme.panelOpacity, 0, 1, 1);
  root.setProperty("--panel-color", hexToRgba(panelColor, panelOpacity, "rgba(17, 20, 28, 0.55)"));
  root.setProperty("--drop-blur", clampNumber(theme.dragBackdropBlur, 0, 80, 42) + "px");
  root.setProperty("--message-list-color", hexToRgba(theme.messageListColor, theme.messageListOpacity, "rgba(0, 0, 0, 0)"));
  root.setProperty("--assistant-bubble-color", hexToRgba(theme.assistantBubbleColor, theme.assistantBubbleOpacity, "rgba(255, 255, 255, 0.10)"));
  root.setProperty("--user-bubble-color", hexToRgba(theme.userBubbleColor, theme.userBubbleOpacity, "rgba(85, 130, 245, 0.25)"));
  root.setProperty("--input-color", hexToRgba(theme.inputColor, theme.inputOpacity, "rgba(255, 255, 255, 0.085)"));
  root.setProperty("--text-color", String(theme.textColor || "#EEF1F6"));

  // 侧栏面板是浮在聊天区上面的，必须比主面板更不透明，否则底下的消息透上来会看不清。
  root.setProperty(
    "--sidebar-panel-color",
    hexToRgba(panelColor, Math.min(1, panelOpacity + 0.34), "rgba(14, 18, 26, 0.94)"));
  root.setProperty(
    "--sidebar-rail-color",
    hexToRgba(panelColor, Math.min(1, panelOpacity + 0.06), "rgba(255, 255, 255, 0.07)"));

  // 侧栏两档宽度由后端下发（QuickChatConfig.SidebarRailWidth / SidebarPanelWidth）。
  // 面板必须是固定像素宽：展开时窗口宽度会跟着变，如果用百分比就成了反馈环。
  root.setProperty(
    "--sidebar-rail-width",
    Math.max(8, Number(theme.sidebarRailWidth) || 28) + "px");
  root.setProperty(
    "--sidebar-panel-width",
    Math.max(80, Number(theme.sidebarPanelWidth) || 176) + "px");
}


function ensureQuickChatImageDirectory() {
  fs.mkdirSync(quickChatImageDirectory, { recursive: true });
  return quickChatImageDirectory;
}

function normalizeLocalPath(value) {
  return path.resolve(String(value || ""));
}

function isImagePath(value) {
  return /^([A-Za-z]:[\\/]|\\\\)[^\n\r<>|*?"']+\.(png|jpe?g|gif|webp|bmp)$/i.test(String(value || "").trim());
}

function isAttachmentPathLine(value) {
  const text = String(value || "").trim();
  if (!text)
    return false;
  if (/^用户(?:发送了|文字)/.test(text))
    return false;
  return true;
}

function pathToFileUrl(value) {
  try {
    return pathToFileURL(normalizeLocalPath(value)).href;
  } catch {
    return "";
  }
}

function sha1(value) {
  return crypto.createHash("sha1").update(String(value)).digest("hex");
}

function thumbnailPathForSource(sourcePath) {
  return path.join(ensureQuickChatImageDirectory(), "thumb-" + sha1(normalizeLocalPath(sourcePath)).slice(0, 16) + ".jpg");
}

function imageExtension(file) {
  const extension = path.extname(file.name || "");
  if (extension)
    return extension.toLowerCase();
  const type = String(file.type || "");
  if (type.startsWith("image/")) {
    const subtype = type.split("/")[1] || "png";
    return subtype === "jpeg" ? ".jpg" : "." + subtype;
  }
  const subtype = type.split("/")[1] || "";
  const cleaned = subtype.split(";")[0].replace(/[^a-z0-9]/gi, "");
  return cleaned ? "." + cleaned : ".bin";
}

function getElectronFilePath(file) {
  if (file.path)
    return normalizeLocalPath(file.path);
  try {
    if (webUtils && typeof webUtils.getPathForFile === "function")
      return normalizeLocalPath(webUtils.getPathForFile(file));
  } catch {
    // Clipboard screenshots do not always expose a source path.
  }
  return "";
}

async function saveTemporaryImage(file) {
  ensureQuickChatImageDirectory();
  const fileName = "file-" + Date.now() + "-" + Math.random().toString(36).slice(2, 8) + imageExtension(file);
  const filePath = path.join(quickChatImageDirectory, fileName);
  const buffer = Buffer.from(await file.arrayBuffer());
  fs.writeFileSync(filePath, buffer);
  return normalizeLocalPath(filePath);
}

async function createThumbnail(file, sourcePath) {
  const thumbnailPath = thumbnailPathForSource(sourcePath);
  try {
    const buffer = getElectronFilePath(file)
      ? fs.readFileSync(getElectronFilePath(file))
      : Buffer.from(await file.arrayBuffer());
    const blob = new Blob([buffer], { type: file.type || "image/png" });
    const bitmap = await createImageBitmap(blob);
    const maxSize = 320;
    const scale = Math.min(1, maxSize / Math.max(bitmap.width, bitmap.height, 1));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d");
    context.drawImage(bitmap, 0, 0, width, height);
    const dataUrl = canvas.toDataURL(file.type === "image/png" ? "image/png" : "image/jpeg", 0.86);
    fs.writeFileSync(thumbnailPath, Buffer.from(dataUrl.split(",")[1], "base64"));
    bitmap.close?.();
    return normalizeLocalPath(thumbnailPath);
  } catch {
    try {
      if (fs.existsSync(thumbnailPath) === false)
        fs.copyFileSync(sourcePath, thumbnailPath);
    } catch {
      // Thumbnail creation is best-effort; the original path is still sent.
    }
    return normalizeLocalPath(thumbnailPath);
  }
}

async function addPendingImage(file, preferTemporary = false) {
  if (!file)
    return;

  const isImageFile = String(file.type || "").startsWith("image/");
  let filePath = preferTemporary ? "" : getElectronFilePath(file);
  if (!filePath)
    filePath = await saveTemporaryImage(file);

  let thumbnailPath = null;
  let previewUrl = "";
  if (isImageFile) {
    thumbnailPath = await createThumbnail(file, filePath);
    previewUrl = pathToFileUrl(thumbnailPath || filePath);
  }

  pendingImages.push({
    id: Date.now() + "-" + Math.random().toString(36).slice(2, 8),
    kind: isImageFile ? "image" : "file",
    name: file.name || path.basename(filePath),
    path: filePath,
    thumbnailPath,
    previewUrl
  });
  renderImagePreview();
  requestResize();
}

function removePendingImage(id) {
  const index = pendingImages.findIndex(image => image.id === id);
  if (index >= 0)
    pendingImages.splice(index, 1);
  renderImagePreview();
  requestResize();
}

function renderImagePreview() {
  if (!imagePreview)
    return;
  imagePreview.replaceChildren();
  imagePreview.classList.toggle("has-images", pendingImages.length > 0);
  if (pendingImages.length === 0)
    return;

  for (const image of pendingImages) {
    const item = document.createElement("div");
    item.className = image.kind === "file" ? "pending-image pending-file" : "pending-image";

    if (image.kind === "file") {
      const label = document.createElement("span");
      label.className = "pending-file-name";
      label.textContent = image.name;
      item.title = image.name;
      item.appendChild(label);
    } else {
      const img = document.createElement("img");
      img.src = image.previewUrl || pathToFileUrl(image.thumbnailPath || image.path);
      img.alt = image.name;
      item.appendChild(img);
    }

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "pending-image-remove";
    removeButton.title = "移除";
    removeButton.textContent = "×";
    removeButton.addEventListener("click", event => {
      event.stopPropagation();
      removePendingImage(image.id);
    });
    item.appendChild(removeButton);
    imagePreview.appendChild(item);
  }
}



function setImageDragActive(active) {
  app.classList.toggle("drag-over", active);
  app.classList.toggle("drop-blur", active);
}

function isFileDragEvent(event) {
  return Array.from(event.dataTransfer?.types || []).includes("Files");
}
function filesFromDataTransfer(dataTransfer) {
  return Array.from(dataTransfer?.files || []).filter(file => file != null);
}

async function addDroppedImages(files) {
  for (const file of files)
    await addPendingImage(file, false);
}

function buildOutgoingText(text) {
  const images = pendingImages.filter(item => item.kind !== "file");
  const files = pendingImages.filter(item => item.kind === "file");
  if (images.length === 0 && files.length === 0)
    return text;

  const lines = [];
  if (images.length > 0) {
    const countText = images.length === 1 ? "一张" : images.length + "张";
    lines.push("用户发送了" + countText + "图片：");
    for (const image of images)
      lines.push(image.path);
  }
  if (files.length > 0) {
    if (lines.length > 0)
      lines.push("");
    const countText = files.length === 1 ? "一个" : files.length + "个";
    lines.push("用户发送了" + countText + "文件：");
    for (const file of files)
      lines.push(file.path);
  }
  lines.push("", "用户文字：");
  if (text.length > 0)
    lines.push(text);
  return lines.join("\n");
}

async function sendDroppedImagesNow(files) {
  await addDroppedImages(files);
  const conversation = getSelectedConversation();
  if (!conversation || pendingImages.length === 0)
    return;

  sendMessage("send", {
    conversationId: conversation.id,
    text: buildOutgoingText(""),
    attachmentPaths: pendingImages.map(item => item.path)
  });
  pendingImages.length = 0;
  renderImagePreview();
  requestResize();
}

function formatScreenshotTimestamp(date) {
  const value = date instanceof Date ? date : new Date();
  const pad = number => String(number).padStart(2, "0");
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())} ${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}`;
}

function screenshotFileTimestamp(date) {
  return formatScreenshotTimestamp(date).replace(/:/g, "-");
}

function openImagePath(filePath) {
  if (!filePath || !shell || typeof shell.openPath !== "function")
    return;
  shell.openPath(filePath).catch(error => console.error("QuickChat open image failed", error));
}

function parseAttachmentPaths(block) {
  return String(block || "")
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => isAttachmentPathLine(line));
}

function parseAttachmentMessageText(text) {
  const value = String(text ?? "");
  const images = [];
  const files = [];
  let rest = value;

  const imageBlock = rest.match(/用户发送了(?:一张|\d+\s*张)图片\s*[:：]\s*\n([\s\S]*?)(?=\n\s*用户发送了|\n\s*用户文字\s*[:：]|$)/i);
  if (imageBlock) {
    for (const line of parseAttachmentPaths(imageBlock[1])) {
      if (isImagePath(line))
        images.push({ path: line, thumbnailPath: thumbnailPathForSource(line) });
    }
    rest = rest.replace(imageBlock[0], "");
  }

  const fileBlock = rest.match(/用户发送了(?:一个|\d+\s*个)文件\s*[:：]\s*\n([\s\S]*?)(?=\n\s*用户发送了|\n\s*用户文字\s*[:：]|$)/i);
  if (fileBlock) {
    for (const line of parseAttachmentPaths(fileBlock[1]))
      files.push({ path: line, name: path.basename(line) });
    rest = rest.replace(fileBlock[0], "");
  }

  const userTextMatch = rest.match(/用户文字\s*[:：]\s*([\s\S]*)$/i);
  const hasAttachments = images.length > 0 || files.length > 0;
  const userText = cleanDisplayText(userTextMatch ? userTextMatch[1] : rest);
  return { text: hasAttachments ? userText : cleanDisplayText(value), images, files };
}

function messageImageUrl(image) {
  if (image.thumbnailPath && fs.existsSync(image.thumbnailPath))
    return pathToFileUrl(image.thumbnailPath);
  return pathToFileUrl(image.path);
}

function cleanDisplayText(text) {
  return String(text ?? "")
    .replace(/<\s*(?:think|thinking|reasoning)\b[^>]*>[\s\S]*?<\s*\/\s*(?:think|thinking|reasoning)\s*>/gi, "")
    .replace(/<[^>\n]{0,300}>/g, "")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .trim();
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function sanitizeMarkdownLink(url) {
  const text = String(url || "").trim();
  if (/^(https?|mailto):/i.test(text))
    return text;
  return "";
}

function renderInlineMarkdown(value) {
  let text = String(value ?? "");
  const codes = [];
  text = text.replace(/`([^`\n]+)`/g, (_m, code) => {
    codes.push(code);
    return " " + codes.length + " ";
  });
  text = escapeHtml(text);
  text = text.replace(/!\[([^\]\n]*)\]\(([^)\s]+)\)/g, (_m, label, url) => {
    const safe = sanitizeMarkdownLink(url);
    return safe ? "<a href=\"" + safe + "\" title=\"" + url + "\">" + (escapeHtml(label) || url) + " ↗</a>" : (escapeHtml(label) || url);
  });
  text = text.replace(/\[([^\]\n]+)\]\(([^)\s]+)\)/g, (_m, label, url) => {
    const safe = sanitizeMarkdownLink(url);
    return safe ? "<a href=\"" + safe + "\" title=\"" + url + "\">" + label + "</a>" : label;
  });
  text = text.replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>");
  text = text.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  text = text.replace(/~~([^~\n]+)~~/g, "<del>$1</del>");
  text = text.replace(/ (\d+) /g, (m, index) => {
    const code = codes[Number(index) - 1];
    return code === undefined ? m : "<code>" + escapeHtml(code) + "</code>";
  });
  return text;
}

function renderMarkdown(value) {
  const lines = String(value ?? "").replace(/\r\n?/g, "\n").split("\n");
  const html = [];
  let paragraph = [];
  let listState = null;
  let quoteLines = null;
  let codeBlock = null;
  let tableState = null;

  const flushParagraph = () => {
    if (paragraph.length === 0)
      return;
    html.push("<p>" + paragraph.map(renderInlineMarkdown).join("<br>") + "</p>");
    paragraph = [];
  };
  const flushList = () => {
    if (listState) {
      html.push(listState.ordered ? "</ol>" : "</ul>");
      listState = null;
    }
  };
  const flushQuote = () => {
    if (quoteLines) {
      html.push("<blockquote>" + quoteLines.map(renderInlineMarkdown).join("<br>") + "</blockquote>");
      quoteLines = null;
    }
  };
  const flushCode = () => {
    if (codeBlock) {
      html.push("<pre><code>" + escapeHtml(codeBlock.lines.join("\n")) + "</code></pre>");
      codeBlock = null;
    }
  };
  const splitTableRow = (line) => {
    const trimmed = line.trim().replace(/^\|/, "").replace(/\|\s*$/, "");
    return trimmed.split("|").map(cell => cell.trim());
  };
  const isTableSeparator = (line) => {
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(cell => /^:?-{3,}:?$/.test(cell));
  };
  const flushTable = () => {
    if (!tableState)
      return;
    const rows = [tableState.header].concat(tableState.rows);
    let tableHtml = "<table>";
    rows.forEach((cells, rowIndex) => {
      const tag = rowIndex === 0 ? "th" : "td";
      tableHtml += "<tr>";
      for (const cell of cells)
        tableHtml += "<" + tag + ">" + renderInlineMarkdown(cell) + "</" + tag + ">";
      tableHtml += "</tr>";
    });
    tableHtml += "</table>";
    html.push(tableHtml);
    tableState = null;
  };
  const flushAll = () => {
    flushParagraph();
    flushList();
    flushQuote();
    flushCode();
    flushTable();
  };

  for (const line of lines) {
    if (codeBlock) {
      if (/^\s*```\s*$/.test(line))
        flushCode();
      else
        codeBlock.lines.push(line);
      continue;
    }

    const fence = line.match(/^\s*```(\S*)\s*$/);
    if (fence) {
      flushAll();
      codeBlock = { lines: [] };
      continue;
    }

    if (/^\s*$/.test(line)) {
      flushAll();
      continue;
    }

    const heading = line.match(/^(#{1,6})\s+(.+)$/);
    if (heading) {
      flushAll();
      const level = heading[1].length;
      html.push("<h" + level + ">" + renderInlineMarkdown(heading[2]) + "</h" + level + ">");
      continue;
    }

    if (/^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) {
      flushAll();
      html.push("<hr>");
      continue;
    }

    const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
    const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
    if (unordered || ordered) {
      flushParagraph();
      flushQuote();
      flushTable();
      const isOrdered = ordered !== null;
      if (!listState || listState.ordered !== isOrdered) {
        flushList();
        html.push(isOrdered ? "<ol>" : "<ul>");
        listState = { ordered: isOrdered };
      }
      html.push("<li>" + renderInlineMarkdown((unordered || ordered)[1]) + "</li>");
      continue;
    }

    if (/^\s*>/.test(line)) {
      flushParagraph();
      flushList();
      flushTable();
      if (!quoteLines)
        quoteLines = [];
      quoteLines.push(line.replace(/^\s*>\s?/, ""));
      continue;
    }

    if (/^\s*\|/.test(line)) {
      if (isTableSeparator(line))
        continue;
      if (!tableState) {
        flushParagraph();
        flushList();
        flushQuote();
        tableState = { header: splitTableRow(line), rows: [] };
      } else {
        tableState.rows.push(splitTableRow(line));
      }
      continue;
    }

    flushList();
    flushQuote();
    flushTable();
    paragraph.push(line);
  }
  flushAll();
  return html.join("");
}

function getMessages(conversationId) {
  const key = conversationId || "";
  if (messagesByConversation.has(key) === false)
    messagesByConversation.set(key, []);
  return messagesByConversation.get(key);
}

function setMessages(conversationId, messages) {
  messagesByConversation.set(conversationId || "", messages || []);
}

function conversationKeyOf(message) {
  return message?.conversationId || message?.petId || "";
}

function appendMessage(message) {
  const key = conversationKeyOf(message);
  let list = getMessages(key);

  // 占位气泡的收尾：同一角色在同一会话里只会有一条「还没定稿」的气泡。
  // 收到这一轮的定稿时（id 是落盘编号，和临时 id 不同），旧的那条必须撤掉，
  // 否则定稿会紧挨着它再冒一个，同一句话看起来说了两遍。
  // 上一轮被新消息打断、没走到定稿的情况，也是靠这条清掉残留。
  //
  // 判据用 provisional 而不是 streaming：模型吐完字之后还会有一个「全文补推」
  // （此时 streaming 已经是 false，但仍是占位气泡，必须等定稿来了才撤）。
  if (message.role === "assistant") {
    const kept = list.filter(item =>
      item.provisional !== true || item.id === message.id || item.petId !== message.petId);
    if (kept.length !== list.length)
      list = kept;
  }

  // 按 id 去重：用户消息是先本地回显、后由后端翻状态的，
  // 同一条会到两次。追加会造成重复气泡，必须替换。
  const index = list.findIndex(item => item.id === message.id);
  if (index >= 0)
    list[index] = message;
  else
    list.push(message);

  setMessages(key, list);
}

function formatTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime()))
    return "";
  return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

function getSelectedConversation() {
  return state.conversations.find(item => item.id === state.selectedId) || null;
}

function isGroupConversation(conversation) {
  return conversation?.kind === "group";
}

/** 会话在界面上的显示名：私聊 = 角色名；群聊 = 群名。 */
function conversationTitleOf(conversation) {
  if (!conversation)
    return "";
  return conversation.title || conversation.id || "";
}

/** 顶栏成员显示：私聊 = 角色名；群聊 = 成员名（逗号分隔）。 */
function conversationMembersOf(conversation) {
  if (!conversation)
    return [];
  const members = Array.isArray(conversation.members) ? conversation.members.filter(Boolean) : [];
  if (members.length > 0)
    return members;
  return conversation.title ? [conversation.title] : [];
}

/**
 * 会话记录里的一行预览文本。
 * 优先用本地已有消息（刚发出去还没回流也能立刻看到），
 * 本地没有就退回后端随会话列表一起带来的摘要 —— 否则「上一个记录」永远是空的。
 */
function lastMessagePreview(conversation) {
  const conversationId = typeof conversation === "string" ? conversation : conversation?.id;
  const messages = messagesByConversation.get(conversationId || "");
  if (messages && messages.length > 0) {
    const last = messages[messages.length - 1];
    const text = cleanDisplayText(last.text || "").replace(/\s+/g, " ").trim();
    if (text.length === 0)
      return { time: formatTime(last.createdAt), text: last.attachments?.length ? "[附件]" : "" };
    return { time: formatTime(last.createdAt), text };
  }

  if (conversation && typeof conversation === "object" && (conversation.lastText || conversation.lastAt))
    return { time: conversation.lastAt || "", text: conversation.lastText || "" };

  return null;
}

function conversationSignature() {
  return JSON.stringify({
    conversations: state.conversations.map(item => [
      item.id, item.kind, item.title, item.busy === true, item.isLobby === true,
      // canDelete / isCustom 决定左下角那两个按钮的样子，变了也得重绘。
      item.canDelete !== false, item.isCustom === true,
      (item.members || []).join(","), item.lastText || "", item.lastAt || ""
    ]),
    groups: (state.groups || []).map(item => [item.id, item.title, item.isLobby === true]),
    selectedId: state.selectedId,
    groupMode: state.groupMode === true,
    enableGroupChat: state.enableGroupChat === true,
    // 记录区要显示最后一条消息的摘要，所以消息变化也要触发重绘。
    previews: state.conversations.map(item => {
      const preview = lastMessagePreview(item);
      return preview ? [item.id, preview.time, preview.text] : [item.id, "", ""];
    })
  });
}

function createConversationButton(conversation, options = {}) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = options.className || "conversation-item";
  if (conversation.id === state.selectedId)
    button.classList.add("selected");
  if (isGroupConversation(conversation))
    button.classList.add("group");
  if (conversation.busy)
    button.classList.add("busy");

  button.style.setProperty("--chip-color", colorFor(conversationTitleOf(conversation)));
  button.title = conversation.busy
    ? `${conversationTitleOf(conversation)} 正在回复`
    : conversationTitleOf(conversation);

  const name = document.createElement("span");
  name.className = "conversation-name";
  name.textContent = conversationTitleOf(conversation);
  button.appendChild(name);

  if (options.showPreview === true) {
    const preview = lastMessagePreview(conversation);
    const previewLine = document.createElement("span");
    previewLine.className = "conversation-preview";
    previewLine.textContent = preview && preview.text ? preview.text : "还没有消息";
    const time = document.createElement("span");
    time.className = "conversation-time";
    time.textContent = preview ? preview.time : "";
    button.append(previewLine, time);
  }

  button.addEventListener("click", () => {
    if (conversation.id !== state.selectedId)
      sendMessage("select-conversation", { conversationId: conversation.id });
    // 选完就收回侧栏，别挡着聊天区。
    collapseSidebarAfterSelect();
  });

  return button;
}

/* ────────────────────────── 侧栏收拉 ────────────────────────── */

// 鼠标离开后不要立刻收回：从轨道移到面板上时中间会短暂「什么都不在上面」，
// 立刻收回会造成面板一开一合的抖动。
const SIDEBAR_COLLAPSE_DELAY = 260;
// 悬停展开的「停留意图」：鼠标停在轨道上够久才弹开，扫过不算。
const SIDEBAR_HOVER_INTENT_DELAY = 220;
let sidebarCollapseTimer = null;
let sidebarExpandTimer = null;
let sidebarPinned = false;
// 已经上报给后端的「展开 + 常驻」组合。只在真的翻转时才发消息，
// 否则鼠标贴着轨道边缘抖动会让窗口反复改尺寸。
let sidebarExpandedReported = false;
let sidebarPinnedReported = false;
// 后端下发的「用户上次把侧栏留在什么状态」只还原一次。
// 每条 state 都强行套用的话，用户刚点开、后端还没收到回执的那一瞬间就会被打回去。
let sidebarRestoredFromHost = false;

/**
 * 把「展开/收起」同步给后端：后端据此把窗口向左加宽、左边缘左移。
 * 面板是往外弹的，所以聊天区宽度不变，只是旁边多长出一块。
 *
 * @param {boolean} remember
 *   这次变化是不是「用户自己点的轨道」。是的话后端会把它记成偏好，下次呼出照此还原。
 *   自动收起（选中会话、最小化成输入条、关掉群聊）传 false —— 那是临时的界面行为，
 *   不该覆盖用户「我要它一直开着」的选择。
 */
function syncSidebarWindow(remember = false) {
  const expanded = sidebar.classList.contains("expanded") || sidebar.classList.contains("pinned");
  const pinned = sidebarPinned === true;
  if (remember !== true && sidebarExpandedReported === expanded && sidebarPinnedReported === pinned)
    return;

  sidebarExpandedReported = expanded;
  sidebarPinnedReported = pinned;
  sendMessage("sidebar", { expanded, pinned, remember: remember === true });
}

function clearSidebarTimers() {
  clearTimeout(sidebarCollapseTimer);
  sidebarCollapseTimer = null;
  clearTimeout(sidebarExpandTimer);
  sidebarExpandTimer = null;
}

function expandSidebar() {
  clearSidebarTimers();
  sidebar.classList.add("expanded");
  syncSidebarWindow();
}

function collapseSidebarNow() {
  clearSidebarTimers();
  sidebar.classList.remove("expanded");
  syncSidebarWindow();
}

/**
 * 悬停展开：鼠标得在轨道上「停住」够久才弹开，扫过不算。
 * 展开会连带把窗口向左撑宽，蹭一下就开的话很干扰，所以加一道停留意图。
 * 只在「侧栏悬停展开」开启时生效（默认关闭）。
 */
function scheduleExpandSidebar() {
  if (state.sidebarExpandOnHover !== true)
    return;
  if (sidebarPinned || sidebar.classList.contains("expanded"))
    return;

  clearTimeout(sidebarExpandTimer);
  sidebarExpandTimer = setTimeout(() => {
    sidebarExpandTimer = null;
    if (state.sidebarExpandOnHover === true && sidebarPinned === false)
      expandSidebar();
  }, SIDEBAR_HOVER_INTENT_DELAY);
}

function scheduleCollapseSidebar() {
  if (sidebarPinned)
    return;
  clearTimeout(sidebarCollapseTimer);
  clearTimeout(sidebarExpandTimer);
  sidebarExpandTimer = null;
  sidebarCollapseTimer = setTimeout(() => {
    sidebarCollapseTimer = null;
    sidebar.classList.remove("expanded");
    syncSidebarWindow();
  }, SIDEBAR_COLLAPSE_DELAY);
}

/** 连常驻状态一起收回。最小化成输入条时用：侧栏被 display:none 藏起来了，
 *  但 pinned 还留着的话，窗口宽度和实际画出来的面板就会对不上。
 *  注意这里不带 remember —— 收起是界面需要，不是用户的偏好。 */
function forceCollapseSidebar() {
  clearSidebarTimers();
  sidebarPinned = false;
  sidebar.classList.remove("expanded", "pinned");
  syncSidebarWindow();
}

/**
 * 页面加载后按后端记录的偏好还原一次侧栏状态。
 * 只认第一条 state，之后就以本地的点击为准（否则用户刚点开就会被后端旧值打回去）。
 */
function restoreSidebarFromHost(expanded) {
  if (sidebarRestoredFromHost)
    return;
  sidebarRestoredFromHost = true;

  if (expanded !== true)
    return;

  sidebarPinned = true;
  sidebar.classList.add("expanded", "pinned");
  // 窗口在创建时已经按「展开」的几何出生了（后端 SavedSidebarExpanded 决定），
  // 所以这里发出去的 expanded:true 是幂等的，不会引发一次多余的窗口加宽。
  syncSidebarWindow();
}

function toggleSidebarPin() {
  sidebarPinned = sidebarPinned !== true;
  clearSidebarTimers();
  sidebar.classList.toggle("pinned", sidebarPinned);

  // 点击模式下「展开」和「常驻」是同一件事：点开就一直开着，再点就收起。
  // 不再走 expandSidebar()/collapseSidebarNow()，因为那两个会把消息按「非用户意图」发出去，
  // 后端就不会记住这次选择。
  sidebar.classList.toggle("expanded", sidebarPinned);
  syncSidebarWindow(true);
}

/** 选中一个会话后自动收回侧栏，好让聊天区完整露出来（常驻时不收）。 */
function collapseSidebarAfterSelect() {
  if (sidebarPinned === false)
    collapseSidebarNow();
}

/**
 * 轨道上的小点：**一条会话一个点**，顺序就是会话列表的顺序。
 *
 * 以前这里只遍历 state.groups（群聊），于是私聊在轨道上完全没有存在感 ——
 * 轨道看上去只有「群」的入口。现在改成遍历 state.conversations：
 * 私聊也有自己的颜色（和气泡、书签同一套 colorFor），顺序跟着会话列表走。
 *
 * 群聊画成小方块、私聊画成圆点，形状上一眼分得开（见 style.css 的 .rail-dot.group）。
 * 点一下直接切过去 —— 轨道本来就是给「快速跳会话」用的。
 */
function renderRailDots() {
  railDots.replaceChildren();

  if (state.enableGroupChat !== true)
    return;

  const conversations = state.conversations || [];
  for (const conversation of conversations.slice(0, 20)) {
    const title = conversationTitleOf(conversation);

    const dot = document.createElement("span");
    dot.className = "rail-dot";
    if (conversation.kind === "group")
      dot.classList.add("group");
    if (conversation.id === state.selectedId)
      dot.classList.add("selected");

    dot.style.setProperty("--chip-color", colorFor(title));
    dot.title = conversation.kind === "group" ? `群聊：${title}` : `私聊：${title}`;
    dot.addEventListener("click", event => {
      // 别让点击冒泡到轨道上 —— 那会顺带把侧栏开合一次。
      event.stopPropagation();
      if (conversation.id !== state.selectedId)
        sendMessage("select-conversation", { conversationId: conversation.id });
    });

    railDots.appendChild(dot);
  }
}

function renderBookmarks() {
  bookmarkBar.replaceChildren();

  if (state.enableGroupChat !== true) {
    bookmarkBar.classList.add("hidden");
    return;
  }
  bookmarkBar.classList.remove("hidden");

  const groups = state.groups || [];
  if (groups.length === 0) {
    const empty = document.createElement("div");
    empty.className = "bookmark-empty";
    empty.textContent = "无群聊";
    bookmarkBar.appendChild(empty);
    return;
  }

  for (const group of groups) {
    const bookmark = createConversationButton(group, { className: "bookmark" });
    bookmarkBar.appendChild(bookmark);
  }
}

function renderSessionRecords() {
  // 上区：聊过的会话（按最近活跃倒序），可以滚动翻看。
  // 重建 DOM 会丢掉滚动位置，所以先记下来再还原。
  const previousScroll = recordHistory.scrollTop;
  recordHistory.replaceChildren();

  const conversations = state.conversations || [];
  if (conversations.length === 0) {
    const empty = document.createElement("div");
    empty.className = "record-empty";
    empty.textContent = "没有已激活桌宠";
    recordHistory.appendChild(empty);
  } else {
    const ordered = conversations;

    for (const conversation of ordered) {
      const item = createConversationButton(conversation, {
        className: "conversation-item",
        showPreview: true
      });
      recordHistory.appendChild(item);
    }
  }

  recordHistory.scrollTop = previousScroll;

  // 下区：当前会话卡片，固定显示在左下角。
  recordCurrent.replaceChildren();
  const selected = getSelectedConversation();
  if (selected) {
    const current = createConversationButton(selected, { className: "conversation-item current" });
    recordCurrent.appendChild(current);
  } else {
    const empty = document.createElement("div");
    empty.className = "record-empty";
    empty.textContent = "未选择会话";
    recordCurrent.appendChild(empty);
  }

  renderRecordActions(selected);
}

/**
 * 当前会话旁边的两个操作。
 *
 * 新建 = 以当前会话为模板另开一条独立会话（私聊 dm:小梦#2、群聊 group:小群A#2）。
 * 新会话与原来那条并存，记录互不影响 —— 这正是「清空后左侧新开的对话记录不受影响」的落点。
 *
 * 第二个按钮的文案跟着「这个会话能不能真的删掉」走，不写死：
 *   - 自建会话（用户在窗口里「新建」出来的、角色自己拉群/开私聊建的）→ 「删除」，真删。
 *   - 受管会话（角色的私聊、配置里的群、公共大厅）→ 「清空」，只清聊天记录。
 *     它们的**存在性由「角色是否激活 / 配置里有没有这个群」决定**：角色激活着，私聊会话就该在，
 *     哪怕一条消息都没有，那也是「空会话」而不是「没有会话」。所以删不掉，也不该删 ——
 *     真删会连带两个后果：角色重新激活也回不来，而且群聊成员是从激活角色推出来的，会莫名少人。
 * 文案和实际行为必须一致，否则用户点「删除」却发现会话还在，只会以为是 bug。
 */
function renderRecordActions(selected) {
  recordActions.replaceChildren();

  if (!selected) {
    recordActions.classList.add("hidden");
    return;
  }
  recordActions.classList.remove("hidden");

  const title = conversationTitleOf(selected);

  const createButton = document.createElement("button");
  createButton.type = "button";
  createButton.className = "record-action";
  createButton.textContent = "新建";
  createButton.title = `以「${title}」为模板另开一条会话（记录各自独立）`;
  createButton.addEventListener("click", () => {
    sendMessage("conversation-create", { conversationId: selected.id });
  });
  recordActions.appendChild(createButton);

  const second = document.createElement("button");
  second.type = "button";

  if (selected.canDelete === false) {
    // 公共大厅 + 角色私聊 + 配置群：只能清空。
    second.className = "record-action";
    second.textContent = "清空";
    second.title = `清空「${title}」的聊天记录（会话保留：它由角色激活状态 / 群配置决定）`;
    second.addEventListener("click", () => {
      openConfirm({
        title: "确认清空会话？",
        text: `将清空「${title}」的全部聊天记录。这条会话由角色激活状态 / 群配置决定，会一直保留，`
          + "所以清空后它还在，只是空的。",
        okText: "清空",
        showPurge: false,
        onConfirm: () => clearConversation(selected.id, true)
      });
    });
  } else {
    // 自建会话：没有别的存在依据，可以真删。
    second.className = "record-action danger";
    second.textContent = "删除";
    second.title = `删除「${title}」及其全部记录`;
    second.addEventListener("click", () => {
      openConfirm({
        title: "确认删除会话？",
        text: `将删除「${title}」及其全部聊天记录，无法恢复。`,
        okText: "删除",
        showPurge: false,
        onConfirm: () => deleteConversation(selected.id)
      });
    });
  }

  recordActions.appendChild(second);
}

/**
 * 找「和某个角色的私聊」会话。群聊顶栏双击角色名时用它。
 * 优先取 ID 恰好等于角色名的那条（默认私聊）；没有就退回成员里含他的私聊
 * （角色互相私聊的线也满足）。都找不到说明这个角色没有私聊线，返回 null 不响应双击。
 */
function findDirectConversation(name) {
  if (!name)
    return null;

  const conversations = state.conversations || [];
  const exact = conversations.find(item => item.kind === "dm" && item.id === name);
  if (exact)
    return exact;

  return conversations.find(item =>
    item.kind === "dm" && (item.members || []).some(member => member === name)) || null;
}

/**
 * 打开确认弹窗。清理和删除会话共用它，文案与是否显示「删除原始记录」勾选框由调用方给。
 * 不给 onConfirm 就是纯提示，确认后什么也不做。
 */
function openConfirm(options) {
  confirmTitle.textContent = options.title || "确认？";
  confirmText.textContent = options.text || "";
  confirmOk.textContent = options.okText || "确定";

  const showPurge = options.showPurge === true;
  confirmPurgeLabel.classList.toggle("hidden", showPurge === false);
  confirmHint.classList.toggle("hidden", showPurge === false);
  if (showPurge)
    confirmPurge.checked = options.purgeDefault !== false;

  confirmHandler = typeof options.onConfirm === "function" ? options.onConfirm : null;

  confirmOverlay.classList.add("visible");
  confirmOverlay.setAttribute("aria-hidden", "false");
  confirmOk.focus();
}

/**
 * 清空当前会话。
 * purge=true（勾选「同时删除原始记录消息」）连磁盘上的记录、以及角色脑子里的记忆一起清；
 * purge=false 只清窗口显示，原始记录和角色记忆都留着。
 * 两种都只动当前这一个会话，左侧别的会话不受影响。
 *
 * 注意：这几个函数必须是顶层函数，不能放进 DOMContentLoaded 里。
 * renderRecordActions 是顶层函数，它在闭包里引用它们 ——
 * 定义在回调内部的话，那里查不到，点按钮会抛 ReferenceError（而且只在运行时才暴露）。
 */
function clearCurrentConversation(purge) {
  clearConversation(state.selectedId, purge);
}

/**
 * 清空指定会话的消息。左下角「清空」和右上角「清理」都走这里。
 * 只影响传进来的这一个会话。
 */
function clearConversation(conversationId, purge) {
  if (!conversationId)
    return;

  setMessages(conversationId, []);
  hasMoreHistory.set(conversationId, false);
  sendMessage("clear", { conversationId, purge });

  // 清的不是当前正在看的那条，就不动界面。
  if (conversationId !== state.selectedId)
    return;

  state.loadedMessageCount = state.maxVisibleMessages;
  state.manualSizeOverride = false;
  renderMessages();
  updateComposer();
  requestResize();
}

/** 删掉一个自建会话连同它的记录。后端删完会重推 state，前端跟着切到剩下的会话。 */
function deleteConversation(conversationId) {
  if (!conversationId)
    return;

  messagesByConversation.delete(conversationId);
  hasMoreHistory.delete(conversationId);
  sendMessage("conversation-delete", { conversationId });
}

/** 顶栏成员显示。群聊换底色，与私聊的名字行做区分。 */
function renderTitlebar() {
  const selected = getSelectedConversation();
  const groupMode = isGroupConversation(selected);

  conversationTitle.classList.toggle("group", groupMode);
  conversationTitle.replaceChildren();

  if (!selected) {
    const empty = document.createElement("span");
    empty.className = "title-empty";
    empty.textContent = "没有可对话的桌宠";
    conversationTitle.appendChild(empty);
    return;
  }

  const members = conversationMembersOf(selected);
  if (groupMode) {
    const label = document.createElement("span");
    label.className = "title-label";
    label.textContent = conversationTitleOf(selected) + " ·";
    conversationTitle.appendChild(label);
  }

  members.slice(0, 12).forEach((member, index) => {
    const chip = document.createElement("span");
    chip.className = "member-chip";
    chip.style.setProperty("--chip-color", colorFor(member));
    chip.textContent = member;

    // 双击角色名跳到跟他的私聊。
    // 找不到对应私聊（角色没激活、或还没有私聊线）就不加 clickable、不绑事件，
    // 免得点半天没反应看起来像坏了。
    const direct = findDirectConversation(member);
    if (direct) {
      chip.classList.add("clickable");
      chip.title = `双击跳到与「${member}」的私聊`;
      chip.addEventListener("dblclick", () => {
        if (direct.id !== state.selectedId)
          sendMessage("select-conversation", { conversationId: direct.id });
      });
    }

    conversationTitle.appendChild(chip);

    if (index < members.length - 1) {
      const separator = document.createElement("span");
      separator.className = "member-separator";
      separator.textContent = "·";
      conversationTitle.appendChild(separator);
    }
  });

  if (members.length > 12) {
    const more = document.createElement("span");
    more.className = "member-separator";
    more.textContent = `等 ${members.length} 位`;
    conversationTitle.appendChild(more);
  }
}

function renderConversations() {
  const signature = conversationSignature();
  if (signature === lastConversationSignature)
    return;

  lastConversationSignature = signature;
  renderRailDots();
  renderBookmarks();
  renderSessionRecords();
  renderTitlebar();
}

function createMessageElement(message) {
  const bubble = document.createElement("article");
  const role = message.role || "assistant";
  bubble.className = "bubble " + role;
  if (message.kind === "group")
    bubble.classList.add("group");

  if (role !== "system") {
    const meta = document.createElement("div");
    meta.className = "message-meta";

    // 名字显示逻辑：私聊显示「我 / 桌宠名」，群聊显示具体发言成员。
    // 两者共用同一行样式，只是群聊会换底色（见 style.css 的 .bubble.group）。
    const displayName = role === "user"
      ? "我"
      : (message.senderName || message.petName || "桌宠");

    const name = document.createElement("span");
    name.className = "role-name";
    name.style.setProperty("--chip-color", colorFor(role === "user" ? "我" : displayName));
    name.textContent = displayName;

    const time = document.createElement("span");
    time.className = "bubble-time";
    time.textContent = formatTime(message.createdAt);
    meta.append(name, time);

    // 送达状态：以前这里什么都没有，消息被系统消息挤掉时用户完全看不出来，
    // 只看到 AI 莫名其妙回了话。现在至少能看见「我发过、但没送到」。
    if (role === "user" && message.deliveryState === "sending") {
      const state = document.createElement("span");
      state.className = "bubble-state";
      state.textContent = "发送中";
      meta.appendChild(state);
    } else if (role === "user" && message.deliveryState === "failed") {
      const state = document.createElement("span");
      state.className = "bubble-state failed";
      state.textContent = "未送达";
      state.title = "这条消息没有进入对话上下文：可能被后一条消息打断，或被系统主动消息挤掉。";
      meta.appendChild(state);
    }

    bubble.appendChild(meta);
  }

  const parsedMessage = parseAttachmentMessageText(message.text);
  const structuredAttachments = Array.isArray(message.attachments)
    ? message.attachments.filter(item => item && item.path)
    : [];
  const images = [
    ...structuredAttachments.filter(item => item.kind === "image" || (!item.kind && isImagePath(item.path))),
    ...parsedMessage.images
  ];
  const files = [
    ...structuredAttachments.filter(item => item.kind !== "image" && (item.kind || isImagePath(item.path) === false)),
    ...parsedMessage.files
  ];
  const hasAttachments = images.length > 0 || files.length > 0;

  if (images.length > 0) {
    const imageBox = document.createElement("div");
    imageBox.className = "message-images";

    for (const image of images.slice(0, 6)) {
      const imageButton = document.createElement("button");
      imageButton.type = "button";
      imageButton.className = "message-image-button";
      imageButton.title = image.path;
      imageButton.addEventListener("click", event => {
        event.stopPropagation();
        openImagePath(image.path);
      });

      const img = document.createElement("img");
      img.src = messageImageUrl(image);
      img.alt = path.basename(image.path || "image");
      imageButton.appendChild(img);
      imageBox.appendChild(imageButton);
    }
    bubble.appendChild(imageBox);
  }

  if (files.length > 0) {
    const fileBox = document.createElement("div");
    fileBox.className = "message-files";

    for (const file of files.slice(0, 8)) {
      const fileButton = document.createElement("button");
      fileButton.type = "button";
      fileButton.className = "message-file-button";
      fileButton.title = file.path;
      fileButton.addEventListener("click", event => {
        event.stopPropagation();
        openImagePath(file.path);
      });

      const icon = document.createElement("span");
      icon.className = "message-file-icon";
      icon.textContent = "📎";
      const name = document.createElement("span");
      name.className = "message-file-name";
      name.textContent = file.name || path.basename(file.path || "file");
      fileButton.append(icon, name);
      fileBox.appendChild(fileButton);
    }
    bubble.appendChild(fileBox);
  }

  const content = document.createElement("div");
  content.className = "message-content";
  const displayText = hasAttachments ? parsedMessage.text : cleanDisplayText(message.text);
  if (displayText || hasAttachments === false) {
    content.innerHTML = renderMarkdown(displayText || "（无文本内容）");
    bubble.appendChild(content);
  }

  // 流式临时气泡：末尾挂一个闪动光标，明确「这句还在生成」。
  if (message.streaming === true) {
    if (content.parentNode !== bubble)
      bubble.appendChild(content);
    appendStreamingCaret(content);
  }

  return bubble;
}

/**
 * 把闪动光标挂到内容末尾。
 * 必须钻进最后一个块级元素（<p>/<li>）里面，否则 <p> 的块级换行会把光标
 * 甩到下一行开头，看起来像多了一个空段落。
 */
function appendStreamingCaret(content) {
  const caret = document.createElement("span");
  caret.className = "streaming-caret";
  caret.setAttribute("aria-hidden", "true");

  const last = content.lastElementChild;
  if (last && /^(P|LI|BLOCKQUOTE|TD|TH)$/.test(last.tagName))
    last.appendChild(caret);
  else
    content.appendChild(caret);
}

function messageSignature(messages) {
  const visibleMessages = state.showAllMessages
    ? messages
    : messages.slice(-state.loadedMessageCount);
  return JSON.stringify({
    conversationId: state.selectedId,
    compact: state.compact,
    loadedMessageCount: state.loadedMessageCount,
    showAllMessages: state.showAllMessages,
    messages: visibleMessages.map(message => [
      message.id, message.role, message.petId, message.petName,
      message.senderName, message.text, message.createdAt,
      message.deliveryState, message.attachments || [],
      // 这两个标记都要进签名：定稿文本可能和最后一个分片一模一样，
      // 只比文本的话签名不变、不会重渲染，末尾那个闪动光标就撤不掉了。
      message.streaming === true, message.provisional === true
    ])
  });
}

function renderMessages(options = {}) {
  const preserveScroll = options.preserveScroll === true;
  const previousBottomDistance = messageList.scrollHeight - messageList.scrollTop;
  const messages = getMessages(state.selectedId);
  messageList.classList.toggle("hidden", state.compact);

  const signature = messageSignature(messages);
  if (signature === lastMessageSignature)
    return;

  lastMessageSignature = signature;
  messageList.replaceChildren();

  if (messages.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-state";
    empty.textContent = state.selectedId ? "开始对话吧。" : "激活桌宠后即可开始对话。";
    messageList.appendChild(empty);

    // 空会话不主动改高度：新建出来的、或刚清空的会话一条消息都没有，
    // 按内容算高度会把窗口压成一条缝（只剩标题栏 + 输入框），而用户马上要在这里打字，
    // 窗口自己缩小非常碍事。保持当前大小即可。
    // 紧凑模式例外 —— 那是用户显式切过去的，必须允许缩。
    if (state.compact)
      requestResize();
    return;
  }
  const visibleMessages = state.showAllMessages
    ? messages
    : messages.slice(-state.loadedMessageCount);
  for (const message of visibleMessages)
    messageList.appendChild(createMessageElement(message));

  if (preserveScroll)
    messageList.scrollTop = Math.max(0, messageList.scrollHeight - previousBottomDistance);
  else
    messageList.scrollTop = messageList.scrollHeight;

  requestResize();
}

function loadOlderMessages() {
  if (state.showAllMessages || state.loadingOlderMessages)
    return;

  const messages = getMessages(state.selectedId);
  if (messages.length <= state.loadedMessageCount)
    return;

  state.loadingOlderMessages = true;
  state.loadedMessageCount += state.maxVisibleMessages;
  renderMessages({ preserveScroll: true });
  requestAnimationFrame(() => {
    state.loadingOlderMessages = false;
  });
}

function sendResizePayload(desired, compact) {
  const height = Math.max(52, Math.round(desired / 2) * 2);
  const signature = `${compact}:${height}`;
  if (signature === lastSentResizeSignature)
    return;

  lastSentResizeSignature = signature;
  sendMessage("resize", { height, compact });
}

function requestResize() {
  if (manualResizeEdge != null)
    return;

  clearTimeout(resizeDebounceTimer);
  resizeDebounceTimer = setTimeout(() => {
    if (manualResizeEdge != null)
      return;

    // Minimal mode is an explicit mode switch, so always allow it to shrink.
    if (state.compact) {
      sendResizePayload(12 + composer.getBoundingClientRect().height, true);
      return;
    }

    if (state.autoHeight === false)
      return;

    if (state.manualSizeOverride) {
      const desired = document.documentElement.scrollHeight;
      if (desired > window.innerHeight) {
        lastSentResizeSignature = "";
        sendMessage("resize", { height: Math.ceil(desired), compact: state.compact });
      }
      return;
    }

    const listStyle = getComputedStyle(messageList);
    let contentHeight = 0;
    for (const child of messageList.children)
      contentHeight += child.getBoundingClientRect().height;

    const gap = Number.parseFloat(listStyle.rowGap) || 0;
    const gaps = Math.max(0, messageList.children.length - 1) * gap;
    const padding = (Number.parseFloat(listStyle.paddingTop) || 0)
      + (Number.parseFloat(listStyle.paddingBottom) || 0);
    const listHeight = contentHeight + gaps + padding;
    const desired = 16 + titlebar.getBoundingClientRect().height
      + composer.getBoundingClientRect().height + listHeight;
    sendResizePayload(desired, state.compact);
  }, 80);
}

function updateComposer() {
  const conversation = getSelectedConversation();
  const groupMode = isGroupConversation(conversation);
  const disabled = conversation == null;
  const signature = JSON.stringify([
    state.selectedId,
    conversationTitleOf(conversation),
    conversation?.busy === true,
    groupMode
  ]);
  if (signature === lastComposerSignature)
    return;

  lastComposerSignature = signature;
  input.disabled = disabled;
  sendButton.disabled = disabled;

  const displayName = conversationTitleOf(conversation);
  compactPetName.style.setProperty("--chip-color", conversation ? colorFor(displayName) : "#8fa3bf");
  compactPetName.replaceChildren();
  const compactName = document.createElement("span");
  compactName.textContent = conversation ? displayName : "无桌宠";
  compactPetName.appendChild(compactName);
  compactPetName.title = conversation
    ? `当前：${displayName}${conversation.busy ? "（回复中）" : ""}`
    : "没有已激活桌宠";

  // 侧栏弹出时聊天区会临时变窄，长 placeholder 会折成两行并被裁掉，所以写短一点，
  // 完整说明放到 title 里。
  input.title = "Enter 发送，Shift+Enter 换行；可直接粘贴图片或文件";
  if (disabled)
    input.placeholder = "没有可对话的桌宠";
  else if (groupMode)
    input.placeholder = "在群里发言，Enter 发送";
  else if (conversation.busy)
    input.placeholder = `${displayName} 正在回复，可打断`;
  else
    input.placeholder = "输入消息，Enter 发送";
}

function selectByOffset(offset) {
  if (state.conversations.length === 0)
    return;

  const currentIndex = Math.max(0, state.conversations.findIndex(item => item.id === state.selectedId));
  const nextIndex = (currentIndex + offset + state.conversations.length) % state.conversations.length;
  const nextConversation = state.conversations[nextIndex];
  if (nextConversation && nextConversation.id !== state.selectedId)
    sendMessage("select-conversation", { conversationId: nextConversation.id });
}

function submitInput() {
  const text = input.value.trim();
  const conversation = getSelectedConversation();
  if (!conversation || (text.length === 0 && pendingImages.length === 0))
    return;

  const outgoingText = buildOutgoingText(text);

  sendMessage("send", {
    conversationId: conversation.id,
    text: outgoingText,
    attachmentPaths: pendingImages.map(item => item.path)
  });
  pendingImages.length = 0;
  renderImagePreview();
  input.value = "";
  input.style.height = "auto";
  input.focus();
  requestResize();
}

ipcRenderer.on(channel, (_event, json) => {
  try {
    const payload = JSON.parse(json);
    switch (payload.type) {
      // 注意：这里【没有】"sidebar" 分支。
      // 侧栏的展开状态现在是「前端说了算、后端只负责记住」：
      // 前端改状态 → 发 sidebar{expanded,pinned,remember} → 后端调窗口几何 + 记偏好。
      // 后端不再反向命令前端收起 —— 那会让「展开就保持展开」在下一次隐藏窗口时被打回。
      case "screenshot-failed":
      case "screenshot-complete":
        if (screenshotButton)
          screenshotButton.disabled = false;
        if (regionScreenshotButton)
          regionScreenshotButton.disabled = false;
        screenshotBusy = false;
        break;

      case "state": {
        state.conversations = payload.conversations || [];
        state.groups = payload.groups || [];
        state.groupMode = payload.groupMode === true;
        state.enableGroupChat = payload.enableGroupChat !== false;
        // 关掉群聊时整列侧栏收起（CSS 里按这个属性隐藏），否则聊天区会被白挤掉一列宽度。
        app.dataset.groups = state.enableGroupChat ? "on" : "off";

        state.sidebarExpandOnHover = payload.sidebarExpandOnHover === true;
        sidebarRail.title = state.sidebarExpandOnHover
          ? "鼠标移入展开；点击可常驻"
          : "点击展开 / 收起会话导航";

        // 先把「用户上次把侧栏留在什么状态」还原回来，再做别的判定。
        // 顺序反了就会被下面那条「悬停展开关掉就收回」当场打回去 —— 用户明明上次开着，这次却是收起的。
        restoreSidebarFromHost(payload.sidebarExpanded === true);

        // 侧栏整列隐藏时窗口不能再留着展开的那部分宽度，否则右侧会空出一块。
        if (state.enableGroupChat !== true)
          forceCollapseSidebar();

        // 悬停展开刚被关掉时，面板若是靠悬停撑开的（没常驻），立刻收回。
        if (state.sidebarExpandOnHover !== true && sidebarPinned === false)
          collapseSidebarNow();
        state.groupWindowWidth = Number(payload.groupWindowWidth) || 620;
        state.groupWindowHeight = Number(payload.groupWindowHeight) || 560;
        applyTheme(payload.theme || {});

        // 只在「后端给的选中项不在列表里」时才回退到第一个会话。
        // 注意判断的是 payload.selectedId（后端的当前选择），不是 state.selectedId（前端的旧值）：
        // 窗口刚打开时前端还是 null，用前者判断会把后端选好的群聊强行改回第一个私聊。
        const incomingSelected = payload.selectedId || null;
        if ((incomingSelected == null ||
             state.conversations.some(item => item.id === incomingSelected) === false) &&
            state.conversations.length > 0) {
          payload.selectedId = state.conversations[0].id;
          sendMessage("select-conversation", { conversationId: payload.selectedId });
        }

        const pageSize = Math.max(1, Number(payload.maxVisibleMessages) || 6);
        state.maxVisibleMessages = pageSize;
        state.loadedMessageCount = pageSize;

        state.showAllMessages = payload.showAllMessages === true;
        state.hideOnEscape = payload.hideOnEscape !== false;
        // 「清理」弹窗里那个勾选框的默认值。
        state.clearIncludesOriginal = payload.clearIncludesOriginal !== false;
        state.autoHeight = payload.autoHeight !== false;
        state.minWindowHeight = Math.max(52, Number(payload.minWindowHeight) || 96);
        state.maxWindowHeight = Math.max(state.minWindowHeight, Number(payload.maxWindowHeight) || 460);

        const selectedChanged = state.selectedId !== payload.selectedId;
        state.selectedId = payload.selectedId || null;
        if (selectedChanged) {
          state.loadedMessageCount = state.maxVisibleMessages;
          // 换了会话就忘掉手动调过的尺寸：否则「只增不减」的手动模式会让窗口
          // 卡在上一个会话（尤其是群聊）的高度上，切回私聊也缩不回来。
          state.manualSizeOverride = false;
          if (messagesByConversation.has(state.selectedId) === false)
            setMessages(state.selectedId, []);
        }

        const stateSignature = JSON.stringify({
          conversations: state.conversations.map(item => [item.id, item.kind, item.title, item.busy === true]),
          groups: state.groups.map(item => [item.id, item.title]),
          selectedId: state.selectedId,
          groupMode: state.groupMode,
          enableGroupChat: state.enableGroupChat,
          maxVisibleMessages: state.maxVisibleMessages,
          loadedMessageCount: state.loadedMessageCount,
          showAllMessages: state.showAllMessages,
          hideOnEscape: state.hideOnEscape,
          autoHeight: state.autoHeight,
          minWindowHeight: state.minWindowHeight,
          maxWindowHeight: state.maxWindowHeight,
          theme: payload.theme || {}
        });

        if (stateSignature === lastStateSignature)
          break;

        lastStateSignature = stateSignature;
        renderConversations();
        renderMessages();
        updateComposer();
        break;
      }

      case "refresh-state":
        lastSentResizeSignature = "";
        sendMessage("ready");
        break;
      case "history": {
        const conversationId = payload.conversationId || payload.petId;
        setMessages(conversationId, payload.messages || []);

        hasMoreHistory.set(conversationId || "", payload.hasMore === true);
        if (conversationId === state.selectedId) {
          state.loadedMessageCount = state.maxVisibleMessages;
          renderMessages();
          updateComposer();
          if ((payload.messages || []).length > 0)
            requestResize();
        }
        break;
      }

      // 向上翻页：后端从磁盘取更早的一页，前端往列表头部插。
      // 必须按高度差补回滚动位置，否则视口会「跳」到最顶端，用户正在看的那几条会跑掉。
      case "history-prepend": {
        const conversationId = payload.conversationId || payload.petId;
        hasMoreHistory.set(conversationId || "", payload.hasMore === true);
        state.loadingOlderMessages = false;

        if (conversationId === state.selectedId) {
          const existing = getMessages(conversationId);
          const known = new Set(existing.map(item => item.id));
          const fresh = (payload.messages || []).filter(item => known.has(item.id) === false);

          if (fresh.length > 0) {
            const previousHeight = messageList.scrollHeight;
            const previousTop = messageList.scrollTop;
            setMessages(conversationId, fresh.concat(existing));
            state.loadedMessageCount += fresh.length;
            renderMessages();
            messageList.scrollTop = previousTop + (messageList.scrollHeight - previousHeight);
          }
        }
        break;
      }

      case "message": {
        appendMessage(payload);
        const conversationId = conversationKeyOf(payload);
        if (conversationId === state.selectedId) {
          const followBottom = messageList.scrollHeight - messageList.scrollTop - messageList.clientHeight < 48;
          renderMessages({ preserveScroll: followBottom === false });
          updateComposer();
        }
        // 会话记录区要更新「最后一条消息」摘要。
        renderConversations();
        break;
      }
    }
  } catch (error) {
    console.error("QuickChat message error", error);
  }
});

document.addEventListener("DOMContentLoaded", () => {
  // 侧栏：默认「点击轨道开关」（窗口会跟着向左变宽，鼠标扫过就撑开一下很干扰）。
  // 只有在配置里打开「侧栏悬停展开」（默认关闭）之后，鼠标移入才会弹开 ——
  // 而且要先停够 SIDEBAR_HOVER_INTENT_DELAY 才算数。
  // 监听挂在 #sidebar（而不是轨道）上：面板是它的子元素，鼠标在面板上时
  // mouseleave 不会触发，面板就不会自己缩回去。
  sidebar.addEventListener("mouseenter", scheduleExpandSidebar);
  sidebar.addEventListener("mouseleave", () => {
    if (state.sidebarExpandOnHover !== true)
      return;
    scheduleCollapseSidebar();
  });
  sidebarRail.addEventListener("click", toggleSidebarPin);
  sidebarRail.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      toggleSidebarPin();
    }
  });

  minimizeButton.addEventListener("click", () => {
    state.compact = true;
    state.manualSizeOverride = false;
    app.dataset.mode = "compact";
    // 输入条模式下侧栏整列被藏起来，先把窗口加宽的那部分收回去。
    forceCollapseSidebar();
    renderMessages();
    updateComposer();
    requestResize();
  });

  restoreButton.addEventListener("click", () => {
    state.compact = false;
    state.manualSizeOverride = false;
    app.dataset.mode = "expanded";
    renderMessages();
    updateComposer();
    requestResize();
  });

  compactPetName.addEventListener("click", () => {
    selectByOffset(1);
  });

  function hideClearConfirm() {
    confirmOverlay.classList.remove("visible");
    confirmOverlay.setAttribute("aria-hidden", "true");
  }

  screenshotButton.addEventListener("click", () => {
    const conversation = getSelectedConversation();
    if (!conversation || screenshotButton.disabled)
      return;

    screenshotButton.disabled = true;
    if (regionScreenshotButton)
      regionScreenshotButton.disabled = true;
    screenshotBusy = true;
    // 群聊没有单一收件角色，交给后端按当前会话推导（取群里第一个激活成员）。
    sendMessage("screenshot-request", isGroupConversation(conversation) ? {} : { petId: conversation.id });
  });

  regionScreenshotButton?.addEventListener("click", () => {
    const conversation = getSelectedConversation();
    if (!conversation || regionScreenshotButton.disabled)
      return;

    screenshotButton.disabled = true;
    regionScreenshotButton.disabled = true;
    screenshotBusy = true;
    sendMessage("screenshot-region-request", isGroupConversation(conversation) ? {} : { petId: conversation.id });
  });

  clearButton.addEventListener("click", () => {
    if (state.selectedId == null)
      return;

    openConfirm({
      title: "确认清理？",
      text: "只清空当前会话，左侧其它会话（包括你新开的）不受影响。",
      okText: "清理",
      showPurge: true,
      // 默认值来自配置页的「清理默认包含原始记录」。
      purgeDefault: state.clearIncludesOriginal,
      onConfirm: purge => clearCurrentConversation(purge)
    });
  });

  confirmOk.addEventListener("click", () => {
    const handler = confirmHandler;
    const purge = confirmPurge.checked === true;
    hideClearConfirm();
    confirmHandler = null;
    if (typeof handler === "function")
      handler(purge);
  });

  confirmCancel.addEventListener("click", () => {
    confirmHandler = null;
    hideClearConfirm();
  });

  composer.addEventListener("submit", event => {
    event.preventDefault();
    submitInput();
  });

  window.addEventListener("keydown", event => {
    if (event.key === "Escape" && confirmOverlay.classList.contains("visible")) {
      confirmHandler = null;
      hideClearConfirm();
      return;
    }

    if (event.key === "Escape" && state.hideOnEscape) {
      sendMessage("hide");
      return;
    }

    if (event.key === "Enter" && event.target === input && event.shiftKey === false) {
      event.preventDefault();
      submitInput();
      return;
    }

    // Numpad 0-9 must remain ordinary input keys. Only the separate arrow keys switch pets.
    if (event.key === "ArrowLeft" || event.key === "ArrowRight" || event.key === "ArrowUp" || event.key === "ArrowDown") {
      const usingModifier = event.altKey || event.ctrlKey || event.metaKey;
      const canSwitch = usingModifier || document.activeElement !== input || input.value.length === 0;
      if (canSwitch) {
        event.preventDefault();
        selectByOffset(event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1);
      }
    }
  });
  for (const handle of document.querySelectorAll(".resize-handle")) {
    const edge = handle.dataset.edge;
    handle.addEventListener("pointerdown", event => {
      if (event.button !== 0)
        return;

      event.preventDefault();
      event.stopPropagation();
      manualResizeEdge = edge;
      state.manualSizeOverride = true;
      handle.setPointerCapture(event.pointerId);
      sendMessage("resize-start", { edge });
    });

    handle.addEventListener("pointermove", event => {
      if (manualResizeEdge == null)
        return;

      pendingResizeMove = true;
      if (resizeMoveScheduled == false) {
        resizeMoveScheduled = true;
        requestAnimationFrame(() => {
          resizeMoveScheduled = false;
          sendResizeMove();
        });
      }
    });

    const endManualResize = event => {
      if (manualResizeEdge == null)
        return;

      if (event.type === "pointerup" && pendingResizeMove != null) {
        sendResizeMove();
      }

      manualResizeEdge = null;
      pendingResizeMove = null;
      if (handle.hasPointerCapture?.(event.pointerId) === true)
        handle.releasePointerCapture(event.pointerId);
      sendMessage("resize-end");
    };

    handle.addEventListener("pointerup", endManualResize);
    handle.addEventListener("pointercancel", endManualResize);
    handle.addEventListener("lostpointercapture", () => {
      if (manualResizeEdge === edge) {
        manualResizeEdge = null;
        pendingResizeMove = null;
        sendMessage("resize-end");
      }
    });
  }

  // 滚到顶部时向后端要一页更早的记录。
  // 旧版本这里是纯前端翻页（只在已经加载到内存的消息里往回看），
  // 接上落盘之后改成真的向后端请求，这样重启后也能一直往前翻。
  messageList.addEventListener("scroll", () => {
    if (state.compact || state.selectedId == null || state.loadingOlderMessages)
      return;
    if (hasMoreHistory.get(state.selectedId) !== true)
      return;
    if (messageList.scrollTop > 40)
      return;

    state.loadingOlderMessages = true;
    sendMessage("load-older", { conversationId: state.selectedId });

    // 兜底解锁：后端万一没有回包（例如会话刚好被删），不要让翻页永久卡死。
    setTimeout(() => {
      state.loadingOlderMessages = false;
    }, 1500);
  });

  lastInputHeight = input.offsetHeight;
  input.addEventListener("input", () => {
    input.style.height = "auto";
    input.style.height = `${Math.min(96, input.scrollHeight)}px`;
    if (input.offsetHeight !== lastInputHeight) {
      lastInputHeight = input.offsetHeight;
      requestResize();
    }
  });

  // 老版本 Electron 的已知问题：页面里存在文本选区时，标题栏的 app-region 拖动会失灵。
  // 指针进入标题栏或按下时清掉选区，保证拖动始终能开始。
  titlebar.addEventListener("mouseenter", () => {
    const selection = window.getSelection?.();
    if (selection && selection.rangeCount > 0)
      selection.removeAllRanges();
  });

  titlebar.addEventListener("mousedown", () => {
    const selection = window.getSelection?.();
    if (selection && selection.rangeCount > 0)
      selection.removeAllRanges();
  });
  attachButton.addEventListener("click", () => {
    imageInput.click();
  });

  imageInput.addEventListener("change", () => {
    for (const file of imageInput.files || [])
      addPendingImage(file, false);
    imageInput.value = "";
  });

  input.addEventListener("paste", event => {
    const files = Array.from(event.clipboardData?.files || []);
    if (files.length === 0)
      return;

    event.preventDefault();
    for (const file of files)
      addPendingImage(file, true);
  });

  let imageDragDepth = 0;

  document.addEventListener("dragenter", event => {
    if (!isFileDragEvent(event))
      return;
    imageDragDepth++;
    setImageDragActive(true);
  });

  document.addEventListener("dragover", event => {
    if (!isFileDragEvent(event))
      return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
    setImageDragActive(true);
  });

  document.addEventListener("dragleave", event => {
    if (!isFileDragEvent(event))
      return;
    imageDragDepth = Math.max(0, imageDragDepth - 1);
    if (imageDragDepth === 0)
      setImageDragActive(false);
  });

  document.addEventListener("drop", event => {
    imageDragDepth = 0;
    setImageDragActive(false);
    const files = filesFromDataTransfer(event.dataTransfer);
    if (files.length === 0)
      return;

    event.preventDefault();
    event.stopPropagation();
    const dropTarget = event.target instanceof Element ? event.target : null;
    if (dropTarget?.closest("#messageList"))
      sendDroppedImagesNow(files);
    else
      addDroppedImages(files);
  });

  renderConversations();
  renderMessages();
  renderImagePreview();
  updateComposer();
  input.focus();
  sendMessage("ready");
});

