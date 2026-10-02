// Best-effort TYPE CHECK of the FullUI Android UI code (firetv/app/src/main/java/dev/fulluisuit/**)
// without the Android SDK or Google Maven. It compiles that code against Compose Multiplatform
// (desktop) 1.8.2, the real Jellyfin SDK, OkHttp and Coil, plus hand-written stubs
// (src/main/kotlin/stubs) for tv-material3, Hilt, lifecycle and the Wholphin classes it calls.
// It is NOT an APK build: it cannot catch problems in the stubs themselves, resources, the manifest,
// Hilt/KSP, R8 or on-device behavior.
//
//   cd firetv/jvm-checks/ui-typecheck && ../../gradlew -p . compileKotlin
//
plugins {
    kotlin("jvm") version "2.4.20"
    kotlin("plugin.compose") version "2.4.20"
    kotlin("plugin.serialization") version "2.4.20"
}
kotlin { compilerOptions { languageVersion = org.jetbrains.kotlin.gradle.dsl.KotlinVersion.KOTLIN_2_3 } }
val cv = "1.8.2"
val lv = "2.9.1"
val ss = "1.3.3"
configurations.all {
    exclude(group = "androidx.arch.core")
    exclude(group = "androidx.compose.runtime", module = "runtime-retain")
    exclude(group = "org.jetbrains.compose.runtime", module = "runtime-retain")
    resolutionStrategy.eachDependency {
        if (requested.group in listOf("org.jetbrains.compose.runtime", "org.jetbrains.compose.foundation", "org.jetbrains.compose.ui", "org.jetbrains.compose.animation", "org.jetbrains.compose.annotation-internal", "org.jetbrains.compose.collection-internal")) {
            useVersion(cv)
        }
        if (requested.group == "org.jetbrains.androidx.lifecycle") useVersion(lv)
        if (requested.group == "org.jetbrains.androidx.savedstate") useVersion(ss)
    }
    resolutionStrategy.dependencySubstitution {
        substitute(module("androidx.annotation:annotation")).using(module("org.jetbrains.compose.annotation-internal:annotation:$cv"))
        substitute(module("androidx.collection:collection")).using(module("org.jetbrains.compose.collection-internal:collection:$cv"))
        substitute(module("androidx.compose.runtime:runtime")).using(module("org.jetbrains.compose.runtime:runtime:$cv"))
        substitute(module("androidx.compose.runtime:runtime-saveable")).using(module("org.jetbrains.compose.runtime:runtime-saveable:$cv"))
        substitute(module("androidx.savedstate:savedstate")).using(module("org.jetbrains.androidx.savedstate:savedstate:$ss"))
        substitute(module("androidx.savedstate:savedstate-compose")).using(module("org.jetbrains.androidx.savedstate:savedstate-compose:$ss"))
        listOf("lifecycle-common", "lifecycle-runtime", "lifecycle-viewmodel", "lifecycle-runtime-compose", "lifecycle-viewmodel-savedstate").forEach {
            substitute(module("androidx.lifecycle:$it")).using(module("org.jetbrains.androidx.lifecycle:$it:$lv"))
        }
    }
}
dependencies {
    implementation("org.jetbrains.compose.runtime:runtime:$cv")
    implementation("org.jetbrains.compose.foundation:foundation:$cv")
    implementation("org.jetbrains.compose.ui:ui:$cv")
    implementation("org.jetbrains.compose.animation:animation:$cv")
    implementation("io.coil-kt.coil3:coil-compose:3.6.3")
    implementation("org.jellyfin.sdk:jellyfin-core:1.7.1")
    implementation("org.jellyfin.sdk:jellyfin-api:1.7.1")
    implementation("org.robolectric:android-all-instrumented:14-robolectric-10818077-i7")
    implementation("org.robolectric:shadowapi:4.14.1")
    implementation("javax.inject:javax.inject:1")
    implementation(platform("com.squareup.okhttp3:okhttp-bom:5.5.0"))
    implementation("com.squareup.okhttp3:okhttp")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.11.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.11.0")
}
sourceSets["main"].kotlin.srcDir("../../fullui-core/src/main/kotlin")
sourceSets["main"].kotlin.srcDir("../../app/src/main/java/dev")
