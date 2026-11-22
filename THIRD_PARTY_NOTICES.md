# 第三方组件与许可

本程序（远景工坊 · L4D2 自动 LOD 工具）以单文件 exe 形式分发。以下第三方组件被编入 exe 或随程序使用。

## gltfpack / meshoptimizer

- 组件：`gltfpack.exe`（Windows x64，版本 v1.3）
- 来源：https://github.com/zeux/meshoptimizer
- 用途：GLB 的拓扑感知减面（`meshopt_simplify`），作为减面后端内置在 exe 中，运行时释放到临时目录调用
- 许可：MIT License

```
MIT License

Copyright (c) 2016-2024 Arseny Kapoulkine

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Blender

Blender 由用户自行安装，不随本程序分发。Blender 是 GNU GPL 软件，许可与源码见
https://www.blender.org/about/license/。本程序仅通过命令行调用用户已安装的 Blender，
不修改、不打包 Blender 本体。

## nekomdl

`nekomdl.exe` 由用户自行提供（可选，仅"生成后试编译"使用），不随本程序分发。
