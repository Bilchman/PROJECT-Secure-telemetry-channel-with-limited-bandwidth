namespace Core;

public enum RejectionCode
{
    None = 0,   // Успішно прийнято
    E_SHORT,    // Пакет коротший за 75 байтів
    E_MAGIC,    // Невірний magic ("TLM1") або version != 1
    E_LENGTH,   // Довжина пакета != 43 + ciphertext_len + 32
    E_HMAC,     // HMAC не збігається (підробка або невірний пароль)
    E_PAYLOAD,  // Порушено інваріанти навантаження (reserved!=0, symbol_count != 12*frame_count)
    E_CODE      // Помилка розпаковки Гаффмана (довжина коду > 15 або обірваний потік)
}

public readonly record struct ReceiveResult(
    RejectionCode Code,
    IReadOnlyList<TelemetryFrame> Frames)
{
    public bool IsAccepted => Code == RejectionCode.None;
}