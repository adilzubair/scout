# Third-party components

Scout was independently implemented after reviewing Intern's public behavior, source and tests at commit `c0bedee4b374b5f177b670016f5ff2b8e5bcf7eb`. The local reference clone is excluded from distribution. No Intern source code, branding assets, or screenshots are included.

The application uses Microsoft .NET/WPF, C#/WinRT and Microsoft.Data.Sqlite (MIT), SQLitePCLRaw (Apache-2.0), and SQLite (public domain). The self-contained build includes their runtime binaries. Windows PDF rendering uses the operating system's PDF API. Consult the packages' upstream license notices for full terms. Setup can download uv (MIT/Apache-2.0) separately.

Optional model setup installs Laya and its dependencies separately. Laya code and checkpoint weights identify their license as Apache-2.0. PyTorch, Transformers, FastAPI, Uvicorn, Hugging Face Hub, and their dependencies retain their respective upstream licenses and installed package metadata. Scout does not redistribute checkpoint weights in its application ZIP.

- Intern reference: https://github.com/dabit3/intern
- Laya model and license metadata: https://huggingface.co/convaiinnovations/laya
- Laya source: https://github.com/NandhaKishorM/laya
- Microsoft.Data.Sqlite: https://github.com/dotnet/efcore
- SQLitePCLRaw: https://github.com/ericsink/SQLitePCL.raw
- SQLite: https://www.sqlite.org/copyright.html
