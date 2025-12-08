# Data Management Service Agent Instructions

## General

* Make only high confidence suggestions when reviewing code changes.
* Never change NuGet.config files unless explicitly asked to.

### Code Quality

* **REQUIRED**: Obey the `.editorconfig` file settings at all times. The project uses:
  * UTF-8 character encoding
  * LF line endings
  * 2-space indentation
  * Spaces for indentation style
  * Final newlines required
  * Trailing whitespace must be trimmed
* **REQUIRED**: run the appropriate build process and correct any build errors with the following scripts:
  * If modifying code in `./src/dms` then run `dotnet build --no-restore ./src/dms/EdFi.DataManagementService.sln`
  * If modifying code in `./src/config` then run `dotnet build --no-restore ./src/config/EdFi.DmsConfigurationService.sln`

## Formatting

* Apply code-formatting style defined in `.editorconfig`.
* Prefer file-scoped namespace declarations and single-line using directives.
* Insert a newline before the opening curly brace of any code block (e.g., after `if`, `for`, `while`, `foreach`, `using`, `try`, etc.).
* Ensure that the final return statement of a method is on its own line.
* Use pattern matching and switch expressions wherever possible.
* Use `nameof` instead of string literals when referring to member names.

### Nullable Reference Types

* Declare variables non-nullable, and check for `null` at entry points.
* Always use `is null` or `is not null` instead of `== null` or `!= null`.
* Trust the C# null annotations and don't add null checks when the type system says a value cannot be null.

### Testing

* We use NUnit tests.
* We use FluentAssertions for assertions.
* Use FakeItEasy for mocking in tests.
* Copy existing style in nearby files for test method names and capitalization.
