// FullUI: JVM-only build that contains just `:fullui-core` (API client, feature detection, row/badge
// mapping and their unit tests). It needs only a JDK and Maven Central / the Gradle plugin portal:
// no Android SDK and no Google Maven. Use it where the full Android build is not possible:
//
//   cd firetv && ./gradlew -p jvm-checks :fullui-core:test
//
pluginManagement {
    repositories {
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        mavenCentral()
    }
    versionCatalogs {
        create("libs") {
            from(files("../gradle/libs.versions.toml"))
        }
    }
}

rootProject.name = "fullui-jvm-checks"
include(":fullui-core")
project(":fullui-core").projectDir = file("../fullui-core")
