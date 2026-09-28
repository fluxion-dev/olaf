using System.Collections.Generic;
using Xunit;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers
{
    public class SpdxLicenseTextsTests
    {
        public static readonly TheoryData<string, string> GetTextCases = new()
        {
            { "AGPL-3.0-only", "GNU AFFERO General Public License Version 3.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation." },
            { "AGPL-1.0-only", "GNU AFFERO General Public License Version 1.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License." },
            { "LGPL-2.0-only", "GNU Lesser General Public License Version 2.0\nThis library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License." },
            { "LGPL-2.1-only", "GNU Lesser General Public License Version 2.1\nThis library is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License." },
            { "LGPL-3.0-only", "GNU Lesser General Public License Version 3.0\nThis library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License." },
            { "GPL-1.0-only", "GNU General Public License Version 1.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License." },
            { "GPL-2.0-only", "GNU General Public License Version 2.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License." },
            { "GPL-3.0-only", "GNU General Public License Version 3.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation." },
            { "BSD-4-Clause", "BSD 4-Clause License (Original BSD)\nRedistribution and use in source and binary forms, with or without modification, are permitted provided that the above copyright notice and this permission notice appear in all copies." },
            { "AAL", "Attribution Assurance License\nCopyright (c) 2002-2007 The Attribution Assurance Licenses Authors. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files." },
            { "Apache-1.1", "Apache License, Version 1.1\nThis product includes software developed by the Apache Software Foundation." },
            { "MPL-1.0", "Mozilla Public License Version 1.0\nThe contents of this file are subject to the Mozilla Public License Version 1.0." },
            { "MPL-1.1", "Mozilla Public License Version 1.1\nThe contents of this file are subject to the Mozilla Public License Version 1.1." },
        };

        [Theory]
        [MemberData(nameof(GetTextCases))]
        public void GetText_NewLicenses_ReturnsExpectedText(string spdxId, string expectedText)
        {
            var result = SpdxLicenseTexts.GetText(spdxId);
            Assert.Equal(expectedText, result);
        }

        // Ensure fallback still works
        [Fact]
        public void GetText_UnknownLicense_ReturnsFallback()
        {
            var result = SpdxLicenseTexts.GetText("UNKNOWN-LICENSE-123");
            Assert.Equal("UNKNOWN-LICENSE-123 license text as declared by the package registry.", result);
        }

        // Issue #71 B1: expanded curated subset (pinned to
        // "SPDX License List 3.29", license-list-data tag v3.29.0; FULL DB
        // owned by #77). Sample of the newer arms resolves non-empty.
        [Fact]
        public void TryGetText_NewSubsetIds_ReturnsNonEmptyText()
        {
            var ids = new[]
            {
                "CDDL-1.0", "EPL-2.0", "Unlicense", "CC0-1.0",
                "BSL-1.0", "Zlib", "OFL-1.1", "0BSD",
            };

            Assert.True(ids.Length >= 4);
            foreach (var id in ids)
            {
                Assert.True(SpdxLicenseTexts.TryGetText(id, out var text), $"subset id missing: {id}");
                Assert.False(string.IsNullOrWhiteSpace(text), $"subset id empty: {id}");
                Assert.Equal(text, SpdxLicenseTexts.GetText(id));
            }
        }

        // Unknown id: TryGetText misses, and the fetcher chain surfaces
        // spdxdb-miss (first-failure-wins when earlier stages are skipped).
        [Fact]
        public async Task TryFetchLicenseTextAsync_UnknownId_ReturnsSpdxDbMiss()
        {
            var handler = new StubHttpMessageHandler((req, _) => StubHttpMessageHandler.NotFound());
            using var http = ResolverTestHelpers.CreateClient(handler);

            Assert.False(SpdxLicenseTexts.TryGetText("NO-SUCH-LICENSE-071", out _));
            var (text, reason) = await LicenseTextFetcher.TryFetchLicenseTextAsync(
                http, tarballUrl: null, licenseUrl: null, "NO-SUCH-LICENSE-071", CancellationToken.None);

            Assert.Null(text);
            Assert.Equal("spdxdb-miss:NO-SUCH-LICENSE-071", reason);
            Assert.Equal(0, handler.CallCount);
        }

        // Air-gap gate: the DB path needs no HttpClient — pure in-memory
        // lookup, zero HTTP by construction (no handler involved at all).
        [Fact]
        public void GetText_DbLookup_ResolvesWithoutHttpClient()
        {
            Assert.StartsWith("MIT License", SpdxLicenseTexts.GetText("MIT"), StringComparison.Ordinal);
            Assert.StartsWith("Apache License", SpdxLicenseTexts.GetText("Apache-2.0"), StringComparison.Ordinal);
            Assert.True(SpdxLicenseTexts.TryGetText("BSD-3-Clause", out var bsd));
            Assert.False(string.IsNullOrWhiteSpace(bsd));
        }

        // Subset-list mirror + pin stability: every documented family arm
        // resolves, and the historical MIT stub stays byte-identical.
        [Fact]
        public void GetText_DocumentedSubset_PinnedStubsStable()
        {
            var familyArms = new[]
            {
                "GPL-2.0-or-later", "GPL-3.0-or-later",
                "LGPL-2.1-or-later", "LGPL-3.0-or-later",
                "AGPL-3.0-or-later", "MIT-0", "Artistic-2.0", "EPL-1.0",
            };

            foreach (var id in familyArms)
            {
                Assert.True(SpdxLicenseTexts.TryGetText(id, out var text), $"subset id missing: {id}");
                Assert.False(string.IsNullOrWhiteSpace(text), $"subset id empty: {id}");
            }

            Assert.Equal(
                "MIT License\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files.",
                SpdxLicenseTexts.GetText("MIT"));
        }
    }
}