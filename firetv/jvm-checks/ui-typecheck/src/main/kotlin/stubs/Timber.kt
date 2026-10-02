package timber.log

object Timber {
    fun v(message: String?, vararg args: Any?) {}
    fun d(message: String?, vararg args: Any?) {}
    fun d(t: Throwable?, message: String?, vararg args: Any?) {}
    fun i(message: String?, vararg args: Any?) {}
    fun w(message: String?, vararg args: Any?) {}
    fun w(t: Throwable?, message: String?, vararg args: Any?) {}
    fun e(message: String?, vararg args: Any?) {}
    fun e(t: Throwable?, message: String?, vararg args: Any?) {}
}
