pluginManagement {
    repositories {
        gradlePluginPortal()
        mavenCentral()
    }
}

plugins { id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0" }

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories { mavenCentral() }
}

rootProject.name = "loadingcache-benchmarks"

include(":caffeine-write")

project(":caffeine-write").projectDir = file("benchmarks/LoadingCache.Benchmarks/caffeine-write")
