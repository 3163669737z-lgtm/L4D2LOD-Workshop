using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LodStudio.Controls
{
    /// <summary>
    /// 界面用到的图标（矢量路径，24×24 设计网格；描边类 stroke 1.6、圆头圆角；填充类只有 Play / Stop）。
    /// 枚举成员名是界面契约，不要改名；新增图标只允许追加在末尾。
    /// </summary>
    public enum IconKind
    {
        Folder,
        File,
        Cube,
        Play,
        Stop,
        Scan,
        Settings,
        Help,
        Info,
        Sun,
        Moon,
        Monitor,
        ChevronDown,
        ChevronRight,
        Close,
        Plus,
        Trash,
        ExternalLink,
        Copy,
        Search,
        Check,
        Warning,
        Error,
        Shield,
        Gauge,
        Terminal,
        Layers,
        ArrowUp,
        ArrowDown,
        Report,
        Refresh,
        Grid,
        Filter,
        DragHandle,
        Restore,
        Maximize,
        Minimize,
        Menu,
        Lock,
        Eye,
        FolderOpen,
        Keyboard,
        Equals,
        ChevronLeft
    }

    /// <summary>图标几何：24×24 网格的路径数据 + 是否填充。</summary>
    public sealed class IconData
    {
        public string PathData;
        public bool Filled;
        public IconData(string pathData, bool filled) { PathData = pathData; Filled = filled; }
    }

    /// <summary>
    /// 图标表：所有图标都是手写的 24×24 路径，统一用 Icon 控件渲染（描边圆头、可随尺寸缩放）。
    /// 设计约定：坐标取 0.1 精度；主体（描边外沿）落在 3.5~20.5 之间，视觉重心尽量在 (12,12)；
    /// 描边类不闭合的线不填充；实心类（播放/停止）标 Filled。所有路径不含图片/字体/emoji。
    /// </summary>
    public static class IconPaths
    {
        private static readonly Dictionary<IconKind, IconData> Map = new Dictionary<IconKind, IconData>
        {
            // —— 文件与对象：折角、标签页、立方体 ——
            { IconKind.Folder, new IconData(
                "M5.2,5.5 H9.0 C9.45,5.5 9.88,5.68 10.2,6.0 L11.1,6.9 C11.4,7.2 11.84,7.4 12.28,7.4 " +
                "H18.8 C19.75,7.4 20.5,8.15 20.5,9.1 V16.9 C20.5,17.85 19.75,18.6 18.8,18.6 H5.2 " +
                "C4.25,18.6 3.5,17.85 3.5,16.9 V7.2 C3.5,6.25 4.25,5.5 5.2,5.5 Z", false) },
            { IconKind.File, new IconData(
                "M6.5,3.3 H13.4 L18.7,8.6 V18.9 C18.7,19.85 17.95,20.6 17.0,20.6 H7.1 " +
                "C6.15,20.6 5.4,19.85 5.4,18.9 V4.4 C5.4,3.9 5.85,3.3 6.5,3.3 Z M13.4,3.3 V8.6 H18.7", false) },
            { IconKind.Cube, new IconData(
                "M12.98,3.9 L19.02,7.0 Q20,7.5 20,8.6 L20,15.4 Q20,16.5 19.02,17.0 L12.98,20.1 " +
                "Q12,20.6 11.02,20.1 L4.98,17.0 Q4,16.5 4,15.4 L4,8.6 Q4,7.5 4.98,7.0 L11.02,3.9 " +
                "Q12,3.4 12.98,3.9 Z M4.96,8.04 L12,12 L19.04,8.04 M12,12 V20.3", false) },

            // —— 播放 / 作业 ——
            { IconKind.Play, new IconData("M8.8,5.7 L18.8,12 L8.8,18.3 Z", true) },
            { IconKind.Stop, new IconData(
                "M8.8,7 H15.2 C16.19,7 17,7.81 17,8.8 V15.2 C17,16.19 16.19,17 15.2,17 H8.8 " +
                "C7.81,17 7,16.19 7,15.2 V8.8 C7,7.81 7.81,7 8.8,7 Z", true) },
            { IconKind.Scan, new IconData(
                "M4.6,9.2 V7.2 C4.6,5.8 5.8,4.6 7.2,4.6 H9.2 M14.8,4.6 H16.8 C18.2,4.6 19.4,5.8 19.4,7.2 V9.2 " +
                "M19.4,14.8 V16.8 C19.4,18.2 18.2,19.4 16.8,19.4 H14.8 M9.2,19.4 H7.2 " +
                "C5.8,19.4 4.6,18.2 4.6,16.8 V14.8 M7.4,12 H16.6", false) },

            // —— 设置 / 帮助 / 状态 ——
            { IconKind.Settings, new IconData(
                // 三根滑杆 + 圆形旋钮：旋钮是描边小圆（半径 1.7，描边后呈 5 单位外径的圆环）
                "M3.8,6 H11.2 M17.6,6 H20.2 " +
                "M14.4,4.3 C15.34,4.3 16.1,5.06 16.1,6 C16.1,6.94 15.34,7.7 14.4,7.7 C13.46,7.7 12.7,6.94 12.7,6 C12.7,5.06 13.46,4.3 14.4,4.3 Z " +
                "M3.8,12 H6.2 M12.6,12 H20.2 " +
                "M9.4,10.3 C10.34,10.3 11.1,11.06 11.1,12 C11.1,12.94 10.34,13.7 9.4,13.7 C8.46,13.7 7.7,12.94 7.7,12 C7.7,11.06 8.46,10.3 9.4,10.3 Z " +
                "M3.8,18 H9.6 M16.0,18 H20.2 " +
                "M12.8,16.3 C13.74,16.3 14.5,17.06 14.5,18 C14.5,18.94 13.74,19.7 12.8,19.7 C11.86,19.7 11.1,18.94 11.1,18 C11.1,17.06 11.86,16.3 12.8,16.3 Z", false) },
            { IconKind.Help, new IconData(
                "M12,3.8 C16.53,3.8 20.2,7.47 20.2,12 C20.2,16.53 16.53,20.2 12,20.2 " +
                "C7.47,20.2 3.8,16.53 3.8,12 C3.8,7.47 7.47,3.8 12,3.8 Z " +
                "M9.5,9.9 C9.5,8.15 10.7,7.0 12.2,7.0 C13.7,7.0 14.9,8.15 14.9,9.6 " +
                "C14.9,11.5 12.2,11.8 12.2,13.4 V15.5 M12.2,17.8 V18.0", false) },
            { IconKind.Info, new IconData(
                "M12,3.8 C16.53,3.8 20.2,7.47 20.2,12 C20.2,16.53 16.53,20.2 12,20.2 " +
                "C7.47,20.2 3.8,16.53 3.8,12 C3.8,7.47 7.47,3.8 12,3.8 Z M12,11.2 V16.3 M12,7.9 V8.1", false) },

            // —— 主题 / 设备 ——
            { IconKind.Sun, new IconData(
                "M12,8.4 C13.99,8.4 15.6,10.01 15.6,12 C15.6,13.99 13.99,15.6 12,15.6 " +
                "C10.01,15.6 8.4,13.99 8.4,12 C8.4,10.01 10.01,8.4 12,8.4 Z " +
                "M12,3.6 V6.0 M18.0,12 H20.4 M12,18.0 V20.4 M3.6,12 H6.0 " +
                "M16.24,7.76 L17.94,6.06 M7.76,7.76 L6.06,6.06 M16.24,16.24 L17.94,17.94 M7.76,16.24 L6.06,17.94", false) },
            { IconKind.Moon, new IconData(
                "M19.97,12.69 C19.7,15.79 17.66,18.45 14.74,19.52 C11.82,20.58 8.54,19.86 6.34,17.66 " +
                "C4.14,15.46 3.42,12.18 4.48,9.26 C5.55,6.34 8.21,4.3 11.31,4.03 " +
                "C9.33,5.47 8.39,7.94 8.9,10.33 C9.41,12.72 11.28,14.59 13.67,15.1 C16.06,15.61 18.53,14.67 19.97,12.69 Z", false) },
            { IconKind.Monitor, new IconData(
                "M4.8,4.8 H19.2 C20.08,4.8 20.8,5.52 20.8,6.4 V13.8 C20.8,14.68 20.08,15.4 19.2,15.4 H4.8 " +
                "C3.92,15.4 3.2,14.68 3.2,13.8 V6.4 C3.2,5.52 3.92,4.8 4.8,4.8 Z M12,15.4 V19.2 M9,19.2 H15", false) },

            // —— 导航 / 窗口 ——
            { IconKind.ChevronDown, new IconData("M6.2,9.2 L12,15 L17.8,9.2", false) },
            { IconKind.ChevronRight, new IconData("M9.2,6.2 L15,12 L9.2,17.8", false) },
            { IconKind.Close, new IconData("M6.4,6.4 L17.6,17.6 M17.6,6.4 L6.4,17.6", false) },
            { IconKind.Plus, new IconData("M12,5.4 V18.6 M5.4,12 H18.6", false) },
            { IconKind.Menu, new IconData("M4.0,7.0 H20.0 M4.0,12 H20.0 M4.0,17.0 H20.0", false) },
            { IconKind.Minimize, new IconData("M4.8,12 H19.2", false) },
            { IconKind.Maximize, new IconData(
                "M6.2,4.4 H17.8 C18.79,4.4 19.6,5.21 19.6,6.2 V17.8 C19.6,18.79 18.79,19.6 17.8,19.6 H6.2 " +
                "C5.21,19.6 4.4,18.79 4.4,17.8 V6.2 C4.4,5.21 5.21,4.4 6.2,4.4 Z", false) },
            { IconKind.Restore, new IconData(
                "M5.4,8.6 H14.2 C14.97,8.6 15.6,9.23 15.6,10 V18.6 C15.6,19.37 14.97,20 14.2,20 H5.4 " +
                "C4.63,20 4,19.37 4,18.6 V10 C4,9.23 4.63,8.6 5.4,8.6 Z " +
                "M8.4,8.4 V5.4 C8.4,4.63 9.03,4 9.8,4 H18.4 C19.17,4 19.8,4.63 19.8,5.4 V14 " +
                "C19.8,14.77 19.17,15.4 18.4,15.4 H15.6", false) },
            { IconKind.ExternalLink, new IconData(
                "M18.4,13.2 V18.4 C18.4,19.3 17.7,20 16.8,20 H5.6 C4.7,20 4,19.3 4,18.4 V7.2 " +
                "C4,6.3 4.7,5.6 5.6,5.6 H10.6 M14.0,3.9 H20.2 V10.1 M20.2,3.9 L12.4,11.7", false) },
            { IconKind.ChevronLeft, new IconData("M14.8,6.2 L9,12 L14.8,17.8", false) },

            // —— 编辑 / 操作 ——
            { IconKind.Trash, new IconData(
                "M4.6,6.6 H19.4 M9.3,6.6 V4.9 C9.3,4.35 9.75,3.9 10.3,3.9 H13.7 " +
                "C14.25,3.9 14.7,4.35 14.7,4.9 V6.6 M6.8,6.6 L7.7,18.9 C7.75,19.7 8.4,20.1 9.2,20.1 H14.8 " +
                "C15.6,20.1 16.25,19.7 16.3,18.9 L17.2,6.6 M10.2,10 V16.6 M13.8,10 V16.6", false) },
            { IconKind.Copy, new IconData(
                "M9.4,8.3 H17.8 C18.63,8.3 19.3,8.97 19.3,9.8 V18.2 C19.3,19.03 18.63,19.7 17.8,19.7 H9.4 " +
                "C8.57,19.7 7.9,19.03 7.9,18.2 V9.8 C7.9,8.97 8.57,8.3 9.4,8.3 Z " +
                "M4.7,14.7 V5.9 C4.7,5.0 5.4,4.3 6.3,4.3 H14.7 C15.6,4.3 16.3,5.0 16.3,5.9 V7.5", false) },
            { IconKind.Search, new IconData(
                "M10.4,4.8 C13.49,4.8 16,7.31 16,10.4 C16,13.49 13.49,16 10.4,16 " +
                "C7.31,16 4.8,13.49 4.8,10.4 C4.8,7.31 7.31,4.8 10.4,4.8 Z M14.36,14.36 L19.4,19.4", false) },
            { IconKind.Check, new IconData("M4.8,12.2 L9.4,16.8 L19.0,7.0", false) },
            { IconKind.Filter, new IconData("M4.0,4.8 H20.0 L14.2,11.8 V19.4 L9.8,17.0 V11.8 Z", false) },
            { IconKind.Grid, new IconData(
                "M4.9,3.6 H8.9 C9.62,3.6 10.2,4.18 10.2,4.9 V8.9 C10.2,9.62 9.62,10.2 8.9,10.2 H4.9 " +
                "C4.18,10.2 3.6,9.62 3.6,8.9 V4.9 C3.6,4.18 4.18,3.6 4.9,3.6 Z " +
                "M15.1,3.6 H19.1 C19.82,3.6 20.4,4.18 20.4,4.9 V8.9 C20.4,9.62 19.82,10.2 19.1,10.2 H15.1 " +
                "C14.38,10.2 13.8,9.62 13.8,8.9 V4.9 C13.8,4.18 14.38,3.6 15.1,3.6 Z " +
                "M4.9,13.8 H8.9 C9.62,13.8 10.2,14.38 10.2,15.1 V19.1 C10.2,19.82 9.62,20.4 8.9,20.4 H4.9 " +
                "C4.18,20.4 3.6,19.82 3.6,19.1 V15.1 C3.6,14.38 4.18,13.8 4.9,13.8 Z " +
                "M15.1,13.8 H19.1 C19.82,13.8 20.4,14.38 20.4,15.1 V19.1 C20.4,19.82 19.82,20.4 19.1,20.4 H15.1 " +
                "C14.38,20.4 13.8,19.82 13.8,19.1 V15.1 C13.8,14.38 14.38,13.8 15.1,13.8 Z", false) },
            { IconKind.DragHandle, new IconData(
                // 2×3 点阵：每点是一个半径 0.6 的小圆，描边 1.6 后呈现为实心圆点
                "M9.2,5.6 C9.53,5.6 9.8,5.87 9.8,6.2 C9.8,6.53 9.53,6.8 9.2,6.8 C8.87,6.8 8.6,6.53 8.6,6.2 C8.6,5.87 8.87,5.6 9.2,5.6 Z " +
                "M9.2,11.4 C9.53,11.4 9.8,11.67 9.8,12 C9.8,12.33 9.53,12.6 9.2,12.6 C8.87,12.6 8.6,12.33 8.6,12 C8.6,11.67 8.87,11.4 9.2,11.4 Z " +
                "M9.2,17.2 C9.53,17.2 9.8,17.47 9.8,17.8 C9.8,18.13 9.53,18.4 9.2,18.4 C8.87,18.4 8.6,18.13 8.6,17.8 C8.6,17.47 8.87,17.2 9.2,17.2 Z " +
                "M14.8,5.6 C15.13,5.6 15.4,5.87 15.4,6.2 C15.4,6.53 15.13,6.8 14.8,6.8 C14.47,6.8 14.2,6.53 14.2,6.2 C14.2,5.87 14.47,5.6 14.8,5.6 Z " +
                "M14.8,11.4 C15.13,11.4 15.4,11.67 15.4,12 C15.4,12.33 15.13,12.6 14.8,12.6 C14.47,12.6 14.2,12.33 14.2,12 C14.2,11.67 14.47,11.4 14.8,11.4 Z " +
                "M14.8,17.2 C15.13,17.2 15.4,17.47 15.4,17.8 C15.4,18.13 15.13,18.4 14.8,18.4 C14.47,18.4 14.2,18.13 14.2,17.8 C14.2,17.47 14.47,17.2 14.8,17.2 Z", false) },
            { IconKind.Lock, new IconData(
                "M7.0,10.05 H17.0 C17.99,10.05 18.8,10.86 18.8,11.85 V18.25 C18.8,19.24 17.99,20.05 17.0,20.05 H7.0 " +
                "C6.01,20.05 5.2,19.24 5.2,18.25 V11.85 C5.2,10.86 6.01,10.05 7.0,10.05 Z " +
                "M8.5,10.05 V7.45 C8.5,6.2 9.17,5.04 10.25,4.42 C11.33,3.79 12.67,3.79 13.75,4.42 " +
                "C14.83,5.04 15.5,6.2 15.5,7.45 V10.05", false) },
            { IconKind.Eye, new IconData(
                "M3.2,12 C5.9,7.2 8.9,5.2 12,5.2 C15.1,5.2 18.1,7.2 20.8,12 " +
                "C18.1,16.8 15.1,18.8 12,18.8 C8.9,18.8 5.9,16.8 3.2,12 Z " +
                "M12,9.1 C13.6,9.1 14.9,10.4 14.9,12 C14.9,13.6 13.6,14.9 12,14.9 " +
                "C10.4,14.9 9.1,13.6 9.1,12 C9.1,10.4 10.4,9.1 12,9.1 Z", false) },

            // —— 语义 / 信息 ——
            { IconKind.Warning, new IconData(
                "M12.8,5.19 Q12,3.8 11.2,5.19 L3.6,18.41 Q2.8,19.8 4.4,19.8 H19.6 Q21.2,19.8 20.4,18.41 Z " +
                "M12,9.3 V14.1 M12,16.9 V17.1", false) },
            { IconKind.Error, new IconData(
                "M12,3.8 C16.53,3.8 20.2,7.47 20.2,12 C20.2,16.53 16.53,20.2 12,20.2 " +
                "C7.47,20.2 3.8,16.53 3.8,12 C3.8,7.47 7.47,3.8 12,3.8 Z " +
                "M8.8,8.8 L15.2,15.2 M15.2,8.8 L8.8,15.2", false) },
            { IconKind.Shield, new IconData(
                "M6.8,3.9 H17.2 C18.0,3.9 18.6,4.5 18.6,5.3 V11.0 C18.6,15.2 15.7,18.6 12,20.3 " +
                "C8.3,18.6 5.4,15.2 5.4,11.0 V5.3 C5.4,4.5 6.0,3.9 6.8,3.9 Z", false) },
            { IconKind.Gauge, new IconData(
                "M6.34,18.86 C4.06,16.57 3.37,13.13 4.61,10.14 C5.85,7.15 8.76,5.2 12,5.2 " +
                "C15.24,5.2 18.15,7.15 19.39,10.14 C20.63,13.13 19.94,16.57 17.66,18.86 " +
                "M12,13.2 L16.2,8.4", false) },
            { IconKind.Report, new IconData(
                "M7.2,3 H16.8 C17.68,3 18.4,3.72 18.4,4.6 V19.4 C18.4,20.28 17.68,21 16.8,21 H7.2 " +
                "C6.32,21 5.6,20.28 5.6,19.4 V4.6 C5.6,3.72 6.32,3 7.2,3 Z " +
                "M8.8,8.4 H15.2 M8.8,12 H15.2 M8.8,15.6 H13.6", false) },
            { IconKind.Terminal, new IconData(
                "M4.8,4.6 H19.2 C20.19,4.6 21,5.41 21,6.4 V17.6 C21,18.59 20.19,19.4 19.2,19.4 H4.8 " +
                "C3.81,19.4 3,18.59 3,17.6 V6.4 C3,5.41 3.81,4.6 4.8,4.6 Z " +
                "M7.2,9.4 L10.1,12.1 L7.2,14.8 M12.6,14.8 H16.6", false) },
            { IconKind.Layers, new IconData(
                "M12,3.6 L20.2,8 L12,12.4 L3.8,8 Z M3.8,12 L12,16.4 L20.2,12 M3.8,16 L12,20.4 L20.2,16", false) },

            // —— 方向 / 同步 ——
            { IconKind.ArrowUp, new IconData("M12,19.0 V5.0 M6.2,10.8 L12,5.0 L17.8,10.8", false) },
            { IconKind.ArrowDown, new IconData("M12,5.0 V19.0 M6.2,13.2 L12,19.0 L17.8,13.2", false) },
            { IconKind.Refresh, new IconData(
                "M19.2,12 C19.2,14.91 17.45,17.54 14.76,18.65 C12.06,19.77 8.97,19.15 6.91,17.09 " +
                "C4.85,15.03 4.23,11.94 5.35,9.24 C6.46,6.55 9.09,4.8 12,4.8 " +
                "C14.02,4.8 16.74,5.6 17.39,6.99 L19.2,9.6 M19.2,4.8 V9.6 H14.4", false) },

            // —— 新增（界面后续会用到的补充图标）——
            { IconKind.FolderOpen, new IconData(
                "M4.0,18.8 V7.0 C4.0,6.1 4.7,5.4 5.6,5.4 H9.2 C9.7,5.4 10.1,5.6 10.4,5.9 L11.4,7.0 " +
                "C11.7,7.3 12.1,7.5 12.6,7.5 H18.4 C19.3,7.5 20.0,8.2 20.0,9.1 V12.8 " +
                "M8.6,12.8 H20.0 L17.8,18.8 H4.3 Z", false) },
            { IconKind.Keyboard, new IconData(
                "M4.6,6.6 H19.4 C20.39,6.6 21.2,7.41 21.2,8.4 V15.6 C21.2,16.59 20.39,17.4 19.4,17.4 H4.6 " +
                "C3.61,17.4 2.8,16.59 2.8,15.6 V8.4 C2.8,7.41 3.61,6.6 4.6,6.6 Z " +
                "M6.6,9.7 H7.3 M9.4,9.7 H10.1 M12.2,9.7 H12.9 M15.0,9.7 H15.7 M17.8,9.7 H18.5 " +
                "M8.6,14.2 H15.4", false) },
            { IconKind.Equals, new IconData("M6.4,9.4 H17.6 M6.4,14.6 H17.6", false) }
        };

        public static IconData Get(IconKind kind)
        {
            IconData d;
            return Map.TryGetValue(kind, out d) ? d : new IconData("M4,4 H20 V20 H4 Z", false);
        }

        public static IEnumerable<KeyValuePair<IconKind, IconData>> All { get { return Map; } }

        public static bool TryGet(IconKind kind, out IconData data) { return Map.TryGetValue(kind, out data); }
    }

    /// <summary>
    /// 图标渲染控件：按 Size 缩放 24×24 路径；描边宽度按比例缩放（视觉重量恒定）；
    /// 颜色取 Foreground（或显式 Brush 属性）。
    /// </summary>
    public sealed class Icon : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            "Kind", typeof(IconKind), typeof(Icon),
            new FrameworkPropertyMetadata(IconKind.File, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size", typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
            "IconBrush", typeof(Brush), typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            "Thickness", typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public IconKind Kind { get { return (IconKind)GetValue(KindProperty); } set { SetValue(KindProperty, value); } }
        public double Size { get { return (double)GetValue(SizeProperty); } set { SetValue(SizeProperty, value); } }
        public Brush IconBrush { get { return (Brush)GetValue(IconBrushProperty); } set { SetValue(IconBrushProperty, value); } }
        /// <summary>描边宽度（24 网格单位）；NaN = 默认 1.6</summary>
        public double Thickness { get { return (double)GetValue(ThicknessProperty); } set { SetValue(ThicknessProperty, value); } }

        private static readonly Dictionary<IconKind, Geometry> GeometryCache = new Dictionary<IconKind, Geometry>();

        protected override Size MeasureOverride(Size availableSize)
        {
            double s = Size;
            if (double.IsNaN(s) || s <= 0) s = 16;
            return new Size(s, s);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var data = IconPaths.Get(Kind);
            var geo = GetGeometry(Kind, data);

            Brush brush = IconBrush;
            if (brush == null)
            {
                brush = TryFindResource("Brush.InkSoft") as Brush;
            }
            if (brush == null) return;

            double s = Size <= 0 ? 16 : Size;
            double k = s / 24.0;
            dc.PushTransform(new ScaleTransform(k, k));
            if (data.Filled)
            {
                // 实心图标：同色细描边把尖角磨圆一点，视觉重量与小尺寸下都更稳
                var pen = new Pen(brush, 1.2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                dc.DrawGeometry(brush, pen, geo);
            }
            else
            {
                double t = double.IsNaN(Thickness) || Thickness <= 0 ? 1.6 : Thickness;
                var pen = new Pen(brush, t)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                dc.DrawGeometry(null, pen, geo);
            }
            dc.Pop();
        }

        private static Geometry GetGeometry(IconKind kind, IconData data)
        {
            Geometry g;
            if (GeometryCache.TryGetValue(kind, out g)) return g;
            try
            {
                g = Geometry.Parse(data.PathData);
                g.Freeze();
            }
            catch
            {
                g = Geometry.Parse("M4,4 H20 V20 H4 Z");
                g.Freeze();
            }
            GeometryCache[kind] = g;
            return g;
        }
    }

    /// <summary>
    /// 图标数据表的运行时校验（供 --icon-sheet / 自测用）：
    /// 路径必须能被 Geometry.Parse、几何非空、包围盒（含 1.6 描边）不越出 24×24，并打印每个图标的包围盒。
    /// </summary>
    public static class IconAudit
    {
        private const double Grid = 24.0;
        private const double StrokeHalf = 0.8;          // 1.6 描边的一半
        private const double SafeMin = StrokeHalf + 0.1;
        private const double SafeMax = Grid - StrokeHalf - 0.1;
        private const double CenterTolerance = 2.0;     // 视觉重心允许的偏移（Play 这类语义偏移靠此宽容）
        private const double MinOptical = 10.0;         // 主体最小视觉尺寸
        private const double MaxOptical = 20.6;         // 主体最大视觉尺寸

        /// <summary>
        /// 校验全部图标并把包围盒表打印到控制台。返回值是硬错误列表（空 = 全部通过）；
        /// 尺寸/重心偏差只打印 "ICON WARN"，不计入返回值，避免无意义的验收失败。
        /// </summary>
        public static List<string> Validate()
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var filled = new List<IconKind>();

            Console.WriteLine("ICON-AUDIT: grid=24 stroke=1.6 safe-area=" + Fmt(SafeMin) + ".." + Fmt(SafeMax));
            Console.WriteLine("ICON-AUDIT: " + "kind".PadRight(14) + " x0..x1".PadRight(16) + "y0..y1".PadRight(16) +
                              "w".PadRight(7) + "h".PadRight(7) + "center".PadRight(15) + "status");

            foreach (var kv in IconPaths.All)
            {
                string name = kv.Key.ToString();
                IconData data = kv.Value;
                if (data == null || string.IsNullOrEmpty(data.PathData))
                {
                    errors.Add(name + ": 路径数据为空");
                    Console.WriteLine("ICON-AUDIT: " + name.PadRight(14) + "<路径数据为空>");
                    continue;
                }

                Geometry geo;
                try
                {
                    geo = Geometry.Parse(data.PathData);
                }
                catch (Exception ex)
                {
                    errors.Add(name + ": 路径解析失败 - " + ex.Message);
                    Console.WriteLine("ICON-AUDIT: " + name.PadRight(14) + "PARSE-ERROR " + OneLine(ex.Message));
                    continue;
                }

                Rect b = geo.Bounds;
                bool degenerate = geo.IsEmpty() || b.IsEmpty ||
                                  (b.Width < 0.01 && b.Height < 0.01) ||
                                  double.IsNaN(b.Width) || double.IsNaN(b.Height);
                if (degenerate)
                {
                    errors.Add(name + ": 几何为空/退化");
                    Console.WriteLine("ICON-AUDIT: " + name.PadRight(14) + "EMPTY-GEOMETRY");
                    continue;
                }

                bool outOfGrid = b.Left < SafeMin - 0.001 || b.Top < SafeMin - 0.001 ||
                                 b.Right > SafeMax + 0.001 || b.Bottom > SafeMax + 0.001;
                string status = "OK";
                if (outOfGrid)
                {
                    errors.Add(name + ": 包围盒越出 24×24（含描边）x=" + Fmt(b.Left) + ".." + Fmt(b.Right) +
                               " y=" + Fmt(b.Top) + ".." + Fmt(b.Bottom));
                    status = "OUT-OF-GRID";
                }

                double cx = b.Left + b.Width / 2.0;
                double cy = b.Top + b.Height / 2.0;
                double optical = Math.Max(b.Width, b.Height);
                if (status == "OK")
                {
                    if (optical < MinOptical) { status = "WARN-SMALL"; warnings.Add(name + ": 主体过小（" + Fmt(optical) + "）"); }
                    else if (optical > MaxOptical) { status = "WARN-LARGE"; warnings.Add(name + ": 主体过大（" + Fmt(optical) + "）"); }
                    else if (Math.Abs(cx - 12.0) > CenterTolerance || Math.Abs(cy - 12.0) > CenterTolerance)
                    {
                        status = "WARN-CENTER";
                        warnings.Add(name + ": 视觉重心偏离 (12,12) 到 (" + Fmt(cx) + "," + Fmt(cy) + ")");
                    }
                }

                if (data.Filled) filled.Add(kv.Key);

                Console.WriteLine("ICON-AUDIT: " + name.PadRight(14) +
                                  " " + (Fmt(b.Left) + ".." + Fmt(b.Right)).PadRight(15) +
                                  " " + (Fmt(b.Top) + ".." + Fmt(b.Bottom)).PadRight(15) +
                                  " " + Fmt(b.Width).PadRight(6) + " " + Fmt(b.Height).PadRight(6) +
                                  " " + ("(" + Fmt(cx) + "," + Fmt(cy) + ")").PadRight(14) + " " + status);
            }

            // 枚举与数据表必须一一对应（漏配会走 Get() 的兜底方块，界面会突然出现方框）
            foreach (IconKind kind in Enum.GetValues(typeof(IconKind)))
            {
                IconData data;
                if (!IconPaths.TryGet(kind, out data)) errors.Add(kind + ": 枚举成员没有图标数据");
            }

            // 填充只允许 Play / Stop 两个实心语义图标
            foreach (IconKind kind in filled)
            {
                if (kind != IconKind.Play && kind != IconKind.Stop)
                    warnings.Add(kind + ": 描边图标被标记为 Filled（约定只允许 Play/Stop 填充）");
            }
            if (!filled.Contains(IconKind.Play) || !filled.Contains(IconKind.Stop))
                errors.Add("Play/Stop 必须为填充图标");
            if (filled.Count != 2)
                warnings.Add("填充图标数量为 " + filled.Count + "（约定为 2：Play/Stop）");

            foreach (string w in warnings) Console.WriteLine("ICON WARN: " + w);
            Console.WriteLine("ICON-AUDIT: " + Count() + " icons checked, " + errors.Count + " errors, " + warnings.Count + " warnings");
            return errors;
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string OneLine(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static int Count()
        {
            int n = 0;
            foreach (var kv in IconPaths.All) n++;
            return n;
        }
    }
}
