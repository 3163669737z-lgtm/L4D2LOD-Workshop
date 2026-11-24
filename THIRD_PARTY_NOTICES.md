# 第三方组件与许可

本程序以 GUI 和 CLI 两个可独立运行的 EXE 分发。以下第三方组件被编入两个入口。

## gltfpack / meshoptimizer

- 组件：`gltfpack.exe`（Windows x64，版本 v1.3）
- 来源：https://github.com/zeux/meshoptimizer
- 本地改动：按连通块减面及外观检查，构建补丁见 `tools/gltfpack-lod.patch`。
- 用途：GLB 的拓扑感知减面（`meshopt_simplifyWithAttributes`）。内置在 EXE 中，运行时释放到临时目录调用。
- 许可：MIT License

```
MIT License

Copyright (c) 2016-2026 Arseny Kapoulkine
Copyright (c) 2018-2021 Johannes Kuhlmann (cgltf)
Copyright (c) 2010 Serge A. Zaitsev (jsmn, included in cgltf)
Copyright (c) 2018-2021 Richard Knight (fast_obj)

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

## Datamodel.NET

- 组件为 DMX 二进制和 KeyValues2 编解码源码。来源为 [Artfunkel/Datamodel.NET](https://github.com/Artfunkel/Datamodel.NET/tree/8ad741f69829d85d9deacddc6a19bb965a83e942)。
- Copyright (c) 2013 Tom Edwards。MIT 许可全文见发布包内的 `Datamodel.NET-LICENSE.txt`，源码中对应 [COPYING](src/ThirdParty/Datamodel/COPYING)。

## nekomdl

`nekomdl.exe` 由用户自行提供。CLI 的手动兼容模式可以显式调用它试编译。编译器由用户单独获取。
