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
    }
}