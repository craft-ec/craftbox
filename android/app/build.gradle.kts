plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.tvtorrent"
    compileSdk = 36

    defaultConfig {
        applicationId = "org.tvtorrent.app"
        minSdk = 21
        targetSdk = 36
        versionCode = 1
        versionName = "1.0.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.leanback:leanback:1.0.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")

    // ExoPlayer (Media3) for video playback
    implementation("androidx.media3:media3-exoplayer:1.5.1")
    implementation("androidx.media3:media3-exoplayer-hls:1.5.1")
    implementation("androidx.media3:media3-ui-leanback:1.5.1")
    implementation("androidx.media3:media3-ui:1.5.1")

    // libtorrent4j for BitTorrent streaming
    implementation("org.libtorrent4j:libtorrent4j:2.1.0-31")
    implementation("org.libtorrent4j:libtorrent4j-android-arm64:2.1.0-31")
    implementation("org.libtorrent4j:libtorrent4j-android-arm:2.1.0-31")
    implementation("org.libtorrent4j:libtorrent4j-android-x86:2.1.0-31")
    implementation("org.libtorrent4j:libtorrent4j-android-x86_64:2.1.0-31")

    // NanoHTTPD for the phone settings page + local stream server
    implementation("org.nanohttpd:nanohttpd:2.3.1")

    // OkHttp for Torznab / TMDB calls
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    // Coil for poster image loading
    implementation("io.coil-kt:coil:2.7.0")

    // Kotlin coroutines
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    // JSON parsing
    implementation("org.json:json:20240303")

    // Glide for Leanback image loading
    implementation("com.github.bumptech.glide:glide:4.16.0")
}
