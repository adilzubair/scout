# Third-party components

Scout was independently implemented after reviewing Intern's public behavior, source and tests at commit `c0bedee4b374b5f177b670016f5ff2b8e5bcf7eb`. The local reference clone is excluded from distribution. No Intern source code, branding assets, or screenshots are included.

The application uses Microsoft .NET/WPF, C#/WinRT and Microsoft.Data.Sqlite (MIT), SQLitePCLRaw (Apache-2.0), and SQLite (public domain). The self-contained build includes their runtime binaries. Windows PDF rendering uses the operating system's PDF API. Consult the packages' upstream license notices for full terms. Setup can download uv (MIT/Apache-2.0) separately.

The optional Windows setup executable is built with Inno Setup 7. Its copyright and project URL remain in the installer; see the [Inno Setup license](https://jrsoftware.org/files/is/license.txt).

Optional model setup installs Laya and its dependencies separately. Laya code and checkpoint weights identify their license as Apache-2.0. PyTorch, Transformers, FastAPI, Uvicorn, Hugging Face Hub, and their dependencies retain their respective upstream licenses and installed package metadata. Scout does not redistribute checkpoint weights in its application ZIP.

Optional descriptive file search additionally downloads Qwen3-4B-Instruct-2507
in Unsloth's Q4_K_M GGUF conversion (Apache-2.0), and llama.cpp's Windows Vulkan
runtime (MIT, with its bundled dependency notices). Repository revisions and
SHA256 checksums are pinned in `runtime/semantic_model.json`. These downloads
remain local and are not included in the Scout ZIP.

- Intern reference: https://github.com/dabit3/intern
- Laya model and license metadata: https://huggingface.co/convaiinnovations/laya
- Laya source: https://github.com/NandhaKishorM/laya
- Qwen model: https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507
- Pinned GGUF conversion: https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF
- llama.cpp runtime: https://github.com/ggml-org/llama.cpp
- Microsoft.Data.Sqlite: https://github.com/dotnet/efcore
- SQLitePCLRaw: https://github.com/ericsink/SQLitePCL.raw
- SQLite: https://www.sqlite.org/copyright.html

## Multimodal development runtime

The experimental multimodal probe downloads the full Google EmbeddingGemma 2
checkpoint separately (Apache-2.0), pinned by revision and artifact checksums in
`runtime/embeddinggemma2_model.json`. Its isolated Python dependencies and
development fixture tools retain their upstream licenses and package metadata.
No model weights, native dependency environment or generated fixture media are
included in Git or the base application package.

PDF page rendering uses the pypdfium2 package and its bundled PDFium dependency.
Media experiments use PyAV with bundled FFmpeg libraries; the probe records their
actual build configuration and reported licenses. The portable LibreOffice
research package is a separately downloaded, checksum-verified artifact whose
automatic extraction has not passed the setup spike. Review the corresponding
source, codec and notice requirements before selecting any of these native
artifacts for product distribution.

See [the multimodal runtime screen](MULTIMODAL_RUNTIME_SCREEN.md) for versions,
measurements and remaining distribution work.
