# Tools / 工具

`make_icon.py` — application icon generator. It draws one 24×24 geometry set into a multi-size `src/Assets/app.ico`, keeping the exe icon and the in-app brand mark consistent.

`make_icon.py` —— 应用图标生成器。用同一套 24×24 几何生成多尺寸 `src/Assets/app.ico`，让 exe 图标与界面品牌标记保持一致。

## Usage / 用法

```bash
python tools/make_icon.py            # build app.ico / 生成 app.ico
python tools/make_icon.py --preview  # preview variants and sizes / 预览变体与尺寸
```

Requirements / 依赖：Python 3 + Pillow.
