# Code Style

Follow the conventions in the surrounding code. Keep C# formatting compatible with `dotnet format` and frontend changes compatible with the ESLint configuration in `webui/eslint.config.js`.

- Keep backend I/O asynchronous and pass cancellation tokens through calls that support them. Use structured logging with named placeholders and keep credentials out of log messages.
- Keep React component props typed and reuse the shared components and styling under `webui/src/`. Use the existing API client for endpoint calls so they share URL and error handling.

Run the checks from the repository root.

```bash
dotnet format --verify-no-changes
npm run lint --prefix webui
```

See [Contributing](https://github.com/Feramance/Torrentarr/blob/master/CONTRIBUTING.md) for pre-commit hooks and PR expectations.
