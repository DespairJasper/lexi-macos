package org.lexi.archive.data

import java.time.Instant
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.util.UUID

internal fun utcNow(): String = Instant.now().truncatedTo(ChronoUnit.MILLIS).toString()

data class ArchiveMetadata(
    val uuid: String = UUID.randomUUID().toString(),
    val sourceType: String = "", val sourceTitle: String = "", val sourceExcerpt: String = "",
    val tags: List<String> = emptyList(), val encounterCount: Int = 1, val revision: Int = 1,
    val createdAtUtc: String = utcNow(), val updatedAtUtc: String = utcNow(),
    val lastEncounteredAtUtc: String = utcNow()
)
data class Entry(
    val id: Long = 0, val word: String = "", val phonetic: String = "",
    val translation: String = "", val definition: String = "", val notes: String = "",
    val stage: Int = 0, val status: String = "learning",
    val createdAt: String = LocalDate.now().toString(),
    val learningStartDate: String = LocalDate.now().toString(),
    val nextReviewDate: String? = LocalDate.now().plusDays(1).toString(),
    val lastReviewedAt: String? = null, val reviewCount: Int = 0,
    val archive: ArchiveMetadata = ArchiveMetadata(), val aiResult: AIResult? = null
)
data class Example(val english: String = "", val chinese: String = "")
data class Phrase(val en: String = "", val zh: String = "")
data class AIResult(
    val examples: List<Example> = emptyList(), val synonyms: List<String> = emptyList(),
    val antonyms: List<String> = emptyList(), val phrases: List<Phrase> = emptyList()
)
data class LookupResult(
    val word: String, val phonetic: String = "", val translation: String = "",
    val definition: String = "", val pos: String = "", val found: Boolean = false
)
