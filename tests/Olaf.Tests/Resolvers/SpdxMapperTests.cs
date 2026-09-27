using System.Collections.Generic;
using Xunit;
using Olaf.Resolvers;

namespace Olaf.Tests.Resolvers
{
    public class SpdxMapperTests
    {
        public static readonly TheoryData<string, string> NormalizeCases = new()
        {
            { "AGPL-3.0-only", "AGPL-3.0-only" },
            { "agpl-3.0", "AGPL-3.0-only" },
            { "agplv3", "AGPL-3.0-only" },
            { "AGPL-1.0-only", "AGPL-1.0-only" },
            { "agpl-1.0", "AGPL-1.0-only" },
            { "agplv1", "AGPL-1.0-only" },
            { "LGPL-2.0-only", "LGPL-2.0-only" },
            { "lgpl-2.0", "LGPL-2.0-only" },
            { "lgplv2", "LGPL-2.0-only" },
            { "GPL-1.0-only", "GPL-1.0-only" },
            { "gpl-1.0", "GPL-1.0-only" },
            { "gplv1", "GPL-1.0-only" },
            { "BSD-4-Clause", "BSD-4-Clause" },
            { "bsd-4-clause", "BSD-4-Clause" },
            { "bsd 4-clause", "BSD-4-Clause" },
            { "bsd original", "BSD-4-Clause" },
            { "AAL", "AAL" },
            { "aal", "AAL" },
            { "attribution assurance license", "AAL" },
            { "Apache-1.1", "Apache-1.1" },
            { "apache 1.1", "Apache-1.1" },
            { "apache-1.1", "Apache-1.1" },
            { "MPL-1.0", "MPL-1.0" },
            { "mpl-1.0", "MPL-1.0" },
            { "mozilla public license 1.0", "MPL-1.0" },
            { "MPL-1.1", "MPL-1.1" },
            { "mpl-1.1", "MPL-1.1" },
            { "mozilla public license 1.1", "MPL-1.1" },
        };

        [Theory]
        [MemberData(nameof(NormalizeCases))]
        public void Normalize_NewLicenses_ReturnsExpectedSpdx(string input, string expected)
        {
            var result = SpdxMapper.Normalize(input);
            Assert.Equal(expected, result);
        }

        public static readonly TheoryData<string, string> FromLicenseUrlCases = new()
        {
            { "https://www.gnu.org/licenses/agpl-3.0.txt", "AGPL-3.0-only" },
            { "https://example.com/agpl.txt", "AGPL-3.0-only" },
            { "https://opensource.org/licenses/AAL", "AAL" },
            { "https://opensource.org/licenses/aal", "AAL" },
            { "https://opensource.org/licenses/bsd-original", "BSD-4-Clause" },
            { "https://example.com/bsd-original.txt", "BSD-4-Clause" },
        };

        [Theory]
        [MemberData(nameof(FromLicenseUrlCases))]
        public void FromLicenseUrl_NewLicenses_ReturnsExpectedSpdx(string url, string expected)
        {
            var result = SpdxMapper.FromLicenseUrl(url);
            Assert.Equal(expected, result);
        }

        public static readonly TheoryData<string, string> FromClassifierCases = new()
        {
            { "Affero General Public License", "AGPL-3.0-only" },
            { "AGPL", "AGPL-3.0-only" },
            { "Attribution Assurance License", "AAL" },
            { "AAL", "AAL" },
            { "Original BSD License", "BSD-4-Clause" },
            { "BSD 4-Clause", "BSD-4-Clause" },
            { "BSD 4", "BSD-4-Clause" },
        };

        [Theory]
        [MemberData(nameof(FromClassifierCases))]
        public void FromClassifier_NewLicenses_ReturnsExpectedSpdx(string classifier, string expected)
        {
            var result = SpdxMapper.FromClassifier(classifier);
            Assert.Equal(expected, result);
        }

        public static readonly TheoryData<string, string> FromLicenseTextCases = new()
        {
            { "Apache License\nVersion 2.0, January 2004\nTERMS AND CONDITIONS", "Apache-2.0" },
            { "Licensed under the Apache License v2.0, see terms", "Apache-2.0" },
            { "Permission is hereby granted, free of charge\nMIT\nWITHOUT WARRANTY OF ANY KIND", "MIT" },
            { "Permission is hereby granted\nMassachusetts Institute of Technology\ndeal in the Software", "MIT" },
            { "Redistribution and use in source and binary forms\nNeither the name of the author", "BSD-3-Clause" },
            { "Redistribution and use in source and binary forms\nTHIS SOFTWARE IS PROVIDED AS IS", "BSD-2-Clause" },
            { "Permission to use, copy, modify, and/or distribute this software for any purpose", "ISC" },
        };

        [Theory]
        [MemberData(nameof(FromLicenseTextCases))]
        public void FromLicenseText_Signatures_ReturnsExpectedSpdx(string content, string expected)
        {
            var result = SpdxMapper.FromLicenseText(content);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Permission is hereby granted, free of charge, to any person obtaining a copy\nof this software\nWITHOUT WARRANTY OF ANY KIND\nAND other text that mentions MIT AND Apache")]
        [InlineData("This is a very long single-line license identifier-like string that exceeds one hundred and twenty characters in length for sure yes indeed")]
        public void Normalize_FullText_ReturnsNull(string fullText)
        {
            var result = SpdxMapper.Normalize(fullText);
            Assert.Null(result);
        }

        [Theory]
        [InlineData(120, false)]
        [InlineData(121, true)]
        public void Normalize_LengthBoundary_GuardsAbove120Chars(int length, bool expectNull)
        {
            var input = new string('x', length);

            var result = SpdxMapper.Normalize(input);

            if (expectNull)
            {
                Assert.Null(result);
            }
            else
            {
                Assert.Equal(input, result);
            }
        }

        [Fact]
        public void Normalize_CarriageReturn_ReturnsNull()
        {
            Assert.Null(SpdxMapper.Normalize("MIT\r\nApache-2.0"));
        }

        [Theory]
        [InlineData("MIT OR Apache-2.0", "MIT OR Apache-2.0")]
        [InlineData("MIT AND Apache-2.0", "MIT AND Apache-2.0")]
        [InlineData("GPL-2.0-only WITH Classpath-exception-2.0", "GPL-2.0-only WITH Classpath-exception-2.0")]
        public void Normalize_ShortCompoundExpression_PassesThrough(string input, string expected)
        {
            var result = SpdxMapper.Normalize(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Normalize_EclipsePublicLicenseLongForm_ReturnsEPL()
        {
            Assert.Equal("EPL-1.0", SpdxMapper.Normalize("Eclipse Public License 1.0"));
            Assert.Equal("EPL-2.0", SpdxMapper.Normalize("Eclipse Public License 2.0"));
        }

        [Fact]
        public void FromLicenseText_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(SpdxMapper.FromLicenseText(null));
            Assert.Null(SpdxMapper.FromLicenseText(""));
            Assert.Null(SpdxMapper.FromLicenseText("   "));
            Assert.Null(SpdxMapper.FromLicenseText("some random text with no license markers"));
        }
    }
}