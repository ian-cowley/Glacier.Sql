# Contributing to Glacier.Sql

We welcome contributions from the community! Whether you are fixing bugs, improving documentation, or adding new features, your help is highly appreciated.

## How to Contribute

1. **Fork the Repository**: Create a personal fork on GitHub.
2. **Clone the Fork**: Clone your fork locally:
   ```bash
   git clone https://github.com/your-username/Glacier.Sql.git
   ```
3. **Create a Feature Branch**: Create a branch off of `main` for your changes:
   ```bash
   git checkout -b feature/my-amazing-feature
   ```
4. **Make and Test Changes**:
   - Ensure all code is cleanly formatted.
   - Run the automated test suite before submitting:
     ```bash
     dotnet test
     ```
5. **Commit and Push**:
   - Keep commit messages clear, concise, and descriptive.
   - Push your changes to your fork.
6. **Open a Pull Request**: Submit your pull request to the upstream `Glacier.Sql` repository.

## Development Guidelines

- **Nullability**: Leverage C# 10 nullable reference types (`#nullable enable`).
- **Docstrings**: Maintain xml docstrings on public-facing APIs.
- **Verification**: Include unit tests validating any newly added SQL keywords, parser AST extensions, or engine handlers in `tests/Glacier.Sql.Tests/SqlEngineTests.cs`.
