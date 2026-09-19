plugins {
    application
    idea
}

group = "local.benchmark"

version = "1.0-SNAPSHOT"

java {
    toolchain {
        languageVersion.set(JavaLanguageVersion.of(25))
        vendor.set(JvmVendorSpec.AZUL)
    }
}

tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.release.set(17)
}

idea {
    module {
        isDownloadJavadoc = true
        isDownloadSources = true
    }
}

dependencyLocking { lockAllConfigurations() }

dependencies {
    implementation(libs.caffeine)
    implementation(libs.jmh.core)
    annotationProcessor(libs.jmh.generator)
}

application { mainClass.set("org.openjdk.jmh.Main") }
