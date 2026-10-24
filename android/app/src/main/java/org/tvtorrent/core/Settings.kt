package org.tvtorrent.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.File

data class IndexerConfig(
    val name: String,
    val url: String,
    val apiKey: String
)

data class Settings(
    val indexers: List<IndexerConfig> = emptyList(),
    val tmdbKey: String = "",
    val bufferLimitMb: Int = 3000
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("indexers", JSONArray().apply {
            indexers.forEach { ix ->
                put(JSONObject().apply {
                    put("name", ix.name)
                    put("url", ix.url)
                    put("apiKey", ix.apiKey)
                })
            }
        })
        put("tmdbKey", tmdbKey)
        put("bufferLimitMb", bufferLimitMb)
    }

    fun save(file: File) {
        file.writeText(toJson().toString(2))
    }

    companion object {
        fun load(file: File): Settings {
            if (!file.exists()) return Settings()
            return try {
                val j = JSONObject(file.readText())
                val arr = j.optJSONArray("indexers") ?: JSONArray()
                val indexers = (0 until arr.length()).map { i ->
                    val o = arr.getJSONObject(i)
                    IndexerConfig(
                        name = o.optString("name", ""),
                        url = o.optString("url", ""),
                        apiKey = o.optString("apiKey", "")
                    )
                }
                Settings(
                    indexers = indexers,
                    tmdbKey = j.optString("tmdbKey", ""),
                    bufferLimitMb = j.optInt("bufferLimitMb", 3000)
                )
            } catch (e: Exception) {
                Settings()
            }
        }
    }
}
