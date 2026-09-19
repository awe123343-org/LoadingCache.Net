plugins {
    alias(libs.plugins.kotlin)
    alias(libs.plugins.kotlin.kapt)
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
    options.release.set(25)
}

idea {
    module {
        isDownloadJavadoc = true
        isDownloadSources = true
    }
}

dependencyLocking { lockAllConfigurations() }

dependencies {
    implementation(platform(libs.kotlin.bom))
    implementation(platform(libs.guava.bom))
    implementation(libs.caffeine)
    implementation(libs.guava)
    implementation(libs.jmh.core)
    kapt(libs.jmh.generator)
}

application { mainClass.set("MainKt") }

kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_25) } }
