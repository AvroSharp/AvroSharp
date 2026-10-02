# Third-party notices

AvroSharp is licensed under the MIT license (see `LICENSE`). Its shipped packages contain no code derived from Apache Avro or other Avro implementations. They do include the third-party code listed below, and this file is packed into each of them.

## Polyfill

The `AvroSharp`, `AvroSharp.Codecs`, `AvroSharp.CodeGen`, `AvroSharp.Generators` and `AvroSharp.Tool` assemblies, and those of the add-on packages (`AvroSharp.Confluent`, `AvroSharp.KafkaFlow`, `AvroSharp.Azure.SchemaRegistry`, `AvroSharp.Aws.Glue` and `AvroSharp.Aws.Glue.Kafka`), include source from [Polyfill](https://github.com/SimonCropp/Polyfill) (a source-only package), compiled in as internal types. It provides newer .NET APIs and language support types on older target frameworks.

```text
MIT License

Copyright (c) Simon Cropp

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

## System.CommandLine

The `AvroSharp.Tool` package includes `System.CommandLine.dll` from [System.CommandLine](https://github.com/dotnet/command-line-api), as a .NET tool package carries its dependencies.

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

## Test data

`tests/TestData/apache-avro/` contains test vectors copied unchanged from [Apache Avro](https://github.com/apache/avro) (tag `release-1.12.2`). They are licensed under the Apache License 2.0; see `tests/TestData/apache-avro/LICENSE.txt` and `NOTICE.txt`. They are used only by the tests and are not distributed in any package.

`tests/TestData/java-avro/` contains container files written by Apache Avro Java's `avro-tools` 1.12.2, derived from Apache's `weather.avro` and from generated data (see its `README.md`). They are licensed under the Apache License 2.0, are used only by the tests, and are not distributed in any package.
