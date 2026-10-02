package androidx.lifecycle

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.GlobalScope

abstract class ViewModel {
    protected open fun onCleared() {}
}

val ViewModel.viewModelScope: CoroutineScope get() = GlobalScope
