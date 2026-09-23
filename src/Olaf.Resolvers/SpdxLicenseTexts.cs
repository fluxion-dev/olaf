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
            _ => $"{spdxId} license text as declared by the package registry.",
        };
    }
}
