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
    }
}