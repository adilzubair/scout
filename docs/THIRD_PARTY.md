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
actual build configuration and reported licenses.

The historical Office compatibility spike separately downloaded the official LibreOffice
26.8.1 Windows x64 MSI, pinned by package length and SHA-256 in
`runtime/office_renderer.json`. Read-only extraction retains the package's
`LICENSE.html`, `NOTICE`, and component-specific notices. LibreOffice identifies
MPL-2.0 as its primary license and includes components under other licenses;
the corresponding source release location is recorded in the manifest.
The failed PortableApps 26.2.4 experiment is historical and is not used by the
historical rendering path. Neither renderer package nor extracted runtime is
included in Git or Scout's base application package. LibreOffice is removed
from the revised add-on's installation and runtime dependencies; its helper
and manifest are excluded from application packaging. These records describe
the earlier development experiment.
[LibreOffice licensing](https://www.libreoffice.org/licenses/),
[26.8.1 source releases](https://download.documentfoundation.org/libreoffice/src/26.8.1/).

The dependency locks establish development compatibility;
they do not complete the distribution review for native/codec dependencies.

The add-on setup separately downloads uv 0.11.27 (MIT/Apache-2.0), pinned by
length and SHA-256 in `runtime/multimodal_setup.json`, and uses its managed
Python 3.12.13 distribution. uv and Python remain in the private add-on directory
and are excluded from the base package. Setup disables global executable aliases
and Windows registry registration. Keep the exact interpreter/dependency notices
and source requirements in the final optional-distribution review.
[uv source and licenses](https://github.com/astral-sh/uv/tree/0.11.27),
[managed Python distributions](https://docs.astral.sh/uv/concepts/python-versions/).

See [the multimodal runtime screen](MULTIMODAL_RUNTIME_SCREEN.md) for versions,
measurements and remaining distribution work.
