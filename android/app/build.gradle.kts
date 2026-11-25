import java.util.Properties

plugins { id("com.android.application") }

// Release signing: keystore.properties is local only (git-ignored). Without it, release builds are unsigned.
val keys = Properties().apply { rootProject.file("keystore.properties").takeIf { it.exists() }?.inputStream()?.use { load(it) } }

android {
    namespace = "io.github.macinek21.drivedeck"
    compileSdk { version = release(37) }
    defaultConfig {
        applicationId = "io.github.macinek21.drivedeck"
        minSdk = 26 // Android 8.0: the last official version for the Galaxy S7
        targetSdk = 37
        versionCode = 6
        versionName = "1.4.1"
    }
    if (keys.isNotEmpty()) signingConfigs.create("release") {
        storeFile = file(keys.getProperty("storeFile"))
        storePassword = keys.getProperty("storePassword")
        keyAlias = keys.getProperty("keyAlias")
        keyPassword = keys.getProperty("keyPassword")
    }
    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            signingConfig = signingConfigs.findByName("release")
        }
    }
    buildFeatures { buildConfig = true }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    lint { abortOnError = true; disable += setOf("SetTextI18n", "HardcodedText") }
}

dependencies {
    implementation("androidx.core:core-ktx:1.19.1")
    implementation("androidx.appcompat:appcompat:1.8.0")
    implementation("androidx.lifecycle:lifecycle-service:2.11.1")
    implementation("androidx.viewpager2:viewpager2:1.1.0")
    val camerax = "1.7.0"
    implementation("androidx.camera:camera-camera2:$camerax")
    implementation("androidx.camera:camera-lifecycle:$camerax")
    implementation("androidx.camera:camera-video:$camerax")
    implementation("androidx.camera:camera-effects:$camerax")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20250517")
}
