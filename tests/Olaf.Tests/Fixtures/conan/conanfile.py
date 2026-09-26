from conan import ConanFile


class OlafFixtureConan(ConanFile):
    name = "olaf-fixture"
    version = "1.0.0"
    requires = "fmt/11.0.2", "zlib/1.3.1"
