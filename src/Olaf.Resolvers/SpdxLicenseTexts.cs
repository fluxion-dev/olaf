namespace Olaf.Resolvers;

internal static class SpdxLicenseTexts
{
    public static string GetText(string spdxId)
    {
        return spdxId switch
        {
            "MIT" => "MIT License\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files.",
            "Apache-2.0" => "Apache License\nVersion 2.0, January 2004\nhttp://www.apache.org/licenses/\nLicensed under the Apache License, Version 2.0.",
            "ISC" => "ISC License\nPermission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted.",
            "BSD-2-Clause" => "BSD 2-Clause License\nRedistribution and use in source and binary forms, with or without modification, are permitted.",
            "BSD-3-Clause" => "BSD 3-Clause License\nRedistribution and use in source and binary forms, with or without modification, are permitted.",
            "MPL-2.0" => "Mozilla Public License Version 2.0\nThis Source Code Form is subject to the terms of the Mozilla Public License.",
            "AGPL-3.0-only" => "GNU AFFERO General Public License Version 3.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation.",
            "AGPL-1.0-only" => "GNU AFFERO General Public License Version 1.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License.",
            "LGPL-2.0-only" => "GNU Lesser General Public License Version 2.0\nThis library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License.",
            "LGPL-2.1-only" => "GNU Lesser General Public License Version 2.1\nThis library is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License.",
            "LGPL-3.0-only" => "GNU Lesser General Public License Version 3.0\nThis library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License.",
            "GPL-1.0-only" => "GNU General Public License Version 1.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License.",
            "GPL-2.0-only" => "GNU General Public License Version 2.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License.",
            "GPL-3.0-only" => "GNU General Public License Version 3.0\nThis program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation.",
            "BSD-4-Clause" => "BSD 4-Clause License (Original BSD)\nRedistribution and use in source and binary forms, with or without modification, are permitted provided that the above copyright notice and this permission notice appear in all copies.",
            "AAL" => "Attribution Assurance License\nCopyright (c) 2002-2007 The Attribution Assurance Licenses Authors. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files.",
            "Apache-1.1" => "Apache License, Version 1.1\nThis product includes software developed by the Apache Software Foundation.",
            "MPL-1.0" => "Mozilla Public License Version 1.0\nThe contents of this file are subject to the Mozilla Public License Version 1.0.",
            "MPL-1.1" => "Mozilla Public License Version 1.1\nThe contents of this file are subject to the Mozilla Public License Version 1.1.",
            _ => $"{spdxId} license text as declared by the package registry.",
        };
    }
}
