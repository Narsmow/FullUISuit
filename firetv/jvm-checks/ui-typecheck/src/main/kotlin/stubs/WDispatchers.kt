package com.github.damontecres.wholphin.util

import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.MainCoroutineDispatcher

object WholphinDispatchers {
    val Main: MainCoroutineDispatcher get() = Dispatchers.Main
    var IO: CoroutineDispatcher = Dispatchers.IO
}
