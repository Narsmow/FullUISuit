import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// FullUI: pure-JVM module (no Android, no Jellyfin SDK). Holds the FullUI API client, DTOs,
// feature detection and row/badge mapping so it can be compiled and unit-tested on any JDK,
// without the Android SDK. See firetv/BUILDING.md.
plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.plugin.serialization)
}

kotlin {
    compilerOptions {
        jvmTarget = JvmTarget.JVM_11
        languageVersion = org.jetbrains.kotlin.gradle.dsl.KotlinVersion.KOTLIN_2_3
    }
}

java {
    sourceCompatibility = JavaVersion.VERSION_11
    targetCompatibility = JavaVersion.VERSION_11
}

dependencies {
    api(platform(libs.okhttp.bom))
    api(libs.okhttp)
    api(libs.kotlinx.serialization.json)
    api(libs.kotlinx.coroutines.core)

    testImplementation(libs.junit)
    testImplementation(libs.okhttp.mockwebserver)
    testImplementation(libs.kotlinx.coroutines.test)
}

tasks.withType<Test>().configureEach {
    useJUnit()
    testLogging {
        events("passed", "failed", "skipped")
        showStandardStreams = false
    }
}
